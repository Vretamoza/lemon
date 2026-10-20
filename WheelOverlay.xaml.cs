using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Lemon.Actions;
using Lemon.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;

namespace Lemon;

/// <summary>
/// Rueda radial de Lemon. Se "arma" (ventana casi invisible a pantalla completa) cuando
/// tocas Shift/Ctrl con el botón del mouse presionado; al recibir DragEnter con archivos,
/// dibuja la rueda centrada en el cursor. La opción se elige por ÁNGULO desde el centro.
///
/// Modo diagnóstico: define la variable de entorno LEMON_DEBUG=1 y la capa se tiñe de rojo.
/// </summary>
public partial class WheelOverlay : Window
{
    private const double InnerR = 72;    // radio del centro (soltar aquí = cancelar)
    private const double OuterR = 180;   // radio exterior de la rueda
    private const double GapDeg = 1.2;   // separación entre sectores

    private static readonly Brush SectorBrush = Solid(0xEE, 0x22, 0x22, 0x22);
    private static readonly Brush AccentBrush = Solid(0xFF, 0xFF, 0xD4, 0x3B);
    private static readonly Brush HubBrush    = Solid(0xF5, 0x16, 0x16, 0x16);
    private static readonly Brush StrokeBrush = Solid(0x33, 0xFF, 0xFF, 0xFF);
    private static readonly Brush TextBrush   = Solid(0xFF, 0xF2, 0xF2, 0xF2);
    private static readonly Brush DarkText    = Solid(0xFF, 0x1A, 0x1A, 0x1A);
    private static readonly Brush MutedBrush  = Solid(0x99, 0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Icons  = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly bool DebugMode = Environment.GetEnvironmentVariable("LEMON_DEBUG") == "1";

    private sealed record Sector(FileAction Action, ShapePath Shape, TextBlock Icon, TextBlock Label);

    private readonly List<Sector> _sectors = new();
    private string[] _files = Array.Empty<string>();
    private Wheel _wheel;
    private Point _center;
    private int _selected = -1;

    /// <summary>True desde DragEnter hasta Drop/DragLeave.</summary>
    public bool HasDrag { get; private set; }

    public event Action<FileAction, string[]>? ActionChosen;

    public WheelOverlay()
    {
        InitializeComponent();
        if (DebugMode) Surface.Background = Solid(0x30, 0xFF, 0x00, 0x00);
    }

    // ───────────────────────── API ─────────────────────────

    public void Arm(Wheel wheel)
    {
        _wheel = wheel;
        HasDrag = false;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        WheelHost.Visibility = Visibility.Collapsed;
        Show();
        Log.Write($"Arm({wheel}) · capa {Width:0}x{Height:0} en ({Left:0},{Top:0})");

        // Fuerza a OLE a reevaluar la ventana bajo el cursor (y enviarnos DragEnter) aunque no te muevas.
        Native.NudgeCursor();
    }

    /// <summary>Tocar la otra tecla con la rueda abierta cambia de rueda.</summary>
    public void SwitchWheel(Wheel wheel)
    {
        if (wheel == _wheel) return;
        _wheel = wheel;
        Log.Write($"SwitchWheel({wheel}) · HasDrag={HasDrag}");
        if (HasDrag) Guard("SwitchWheel", BuildWheel);
    }

    public void Disarm(string reason)
    {
        Log.Write($"Disarm: {reason}");
        HasDrag = false;
        _selected = -1;
        _sectors.Clear();
        WheelHost.Children.Clear();
        WheelHost.Visibility = Visibility.Collapsed;
        Hide();
    }

    // ───────────────────────── Eventos de drag ─────────────────────────
    // Las excepciones dentro de estos handlers NO llegan al manejador global (la llamada viene
    // de COM y WPF se las traga), por eso cada uno va envuelto en Guard() con log.

    private void Surface_DragEnter(object sender, DragEventArgs e) => Guard("DragEnter", () =>
    {
        HasDrag = true;
        _files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        _center = e.GetPosition(Surface);
        Log.Write($"DragEnter · {_files.Length} archivo(s) · formatos=[{string.Join(", ", e.Data.GetFormats())}] · pos={_center}");

        BuildWheel();
        UpdateSelection(e);
        DragImage.Enter(this, e.Data, e.Effects);
        e.Handled = true;
    });

    private void Surface_DragOver(object sender, DragEventArgs e) => Guard("DragOver", () =>
    {
        UpdateSelection(e);
        DragImage.Over(e.Effects);
        e.Handled = true;
    });

    // Como la capa cubre toda la pantalla, DragLeave solo ocurre al cancelar (Esc,
    // soltar en el centro) o al terminar el arrastre.
    private void Surface_DragLeave(object sender, DragEventArgs e) => Guard("DragLeave", () =>
    {
        DragImage.Leave();
        Disarm("DragLeave");
    });

    private void Surface_Drop(object sender, DragEventArgs e) => Guard("Drop", () =>
    {
        e.Handled = true;
        var chosen = _selected >= 0 ? _sectors[_selected].Action : null;
        // SIEMPRE Copy: con Shift el Explorador entiende "mover" y borraría el original si respondiéramos Move.
        e.Effects = chosen is null ? DragDropEffects.None : DragDropEffects.Copy;
        DragImage.Drop(e.Data, e.Effects);

        var files = _files;
        Disarm($"Drop → {chosen?.Id ?? "cancelado"}");
        if (chosen is not null) ActionChosen?.Invoke(chosen, files);
    });

    private void UpdateSelection(DragEventArgs e)
    {
        var index = HitTest(e.GetPosition(Surface));
        if (index != _selected)
        {
            if (_selected >= 0) Paint(_sectors[_selected], hot: false);
            if (index >= 0) Paint(_sectors[index], hot: true);
            _selected = index;
        }
        e.Effects = index >= 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private int HitTest(Point p)
    {
        if (_sectors.Count == 0) return -1;
        var dx = p.X - _center.X;
        var dy = p.Y - _center.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < InnerR) return -1;

        var step = 360.0 / _sectors.Count;
        var angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360; // 0° arriba, sentido horario
        return (int)(((angle + step / 2) % 360) / step) % _sectors.Count;
    }

    // ───────────────────────── Dibujo ─────────────────────────

    private void BuildWheel()
    {
        WheelHost.Children.Clear();
        _sectors.Clear();
        _selected = -1;

        _center = KeepOnScreen(_center);
        Canvas.SetLeft(WheelHost, _center.X);
        Canvas.SetTop(WheelHost, _center.Y);

        var actions = ActionRegistry.ForFiles(_files, _wheel);
        Log.Write($"BuildWheel({_wheel}) · {actions.Count} acciones · centro={_center}");
        var step = actions.Count > 0 ? 360.0 / actions.Count : 0;

        // Disco de fondo con la sombra (la sombra va en un elemento con tamaño real,
        // no en el Canvas contenedor, que mide 0×0).
        var backdrop = new Ellipse
        {
            Width = OuterR * 2, Height = OuterR * 2,
            Fill = actions.Count > 0 ? Solid(0xCC, 0x10, 0x10, 0x10) : Brushes.Transparent,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 0, Opacity = 0.55 },
        };
        Canvas.SetLeft(backdrop, -OuterR);
        Canvas.SetTop(backdrop, -OuterR);
        WheelHost.Children.Add(backdrop);

        // Sectores
        for (var i = 0; i < actions.Count; i++)
        {
            var mid = i * step;
            var shape = new ShapePath
            {
                Data = Wedge(mid - step / 2 + GapDeg, mid + step / 2 - GapDeg),
                Fill = SectorBrush, Stroke = StrokeBrush, StrokeThickness = 1,
            };
            var icon = new TextBlock
            {
                Text = actions[i].Glyph, FontFamily = Icons, FontSize = 20,
                Foreground = AccentBrush, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = actions[i].Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            };
            WheelHost.Children.Add(shape);
            // Con muchas opciones los sectores son más angostos: ajustamos el ancho del texto.
            var labelWidth = Math.Min(92, 2 * Math.PI * ((InnerR + OuterR) / 2) / actions.Count - 6);
            PlaceCentered(new StackPanel { Width = labelWidth, Children = { icon, label } },
                          Polar((InnerR + OuterR) / 2, mid));
            _sectors.Add(new Sector(actions[i], shape, icon, label));
        }

        // Centro: icono del tipo de archivo + nombre + rueda actual
        var hubR = InnerR - 6;
        var hub = new Ellipse
        {
            Width = hubR * 2, Height = hubR * 2,
            Fill = HubBrush, Stroke = StrokeBrush, StrokeThickness = 1,
        };
        Canvas.SetLeft(hub, -hubR);
        Canvas.SetTop(hub, -hubR);
        WheelHost.Children.Add(hub);

        var title = _files.Length switch
        {
            0 => "Sin archivos",
            1 => Path.GetFileName(_files[0]),
            var n => $"{n} archivos",
        };
        PlaceCentered(new StackPanel
        {
            Width = hubR * 2 - 18,
            Children =
            {
                new TextBlock { Text = KindGlyph(), FontFamily = Icons, FontSize = 22,
                                Foreground = AccentBrush, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = title, FontSize = 11.5, Foreground = TextBrush,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) },
                new TextBlock { Text = actions.Count == 0 ? "No soportado" : WheelName(_wheel),
                                FontSize = 10.5, Foreground = MutedBrush, TextAlignment = TextAlignment.Center },
            },
        }, new Point(0, 0));

        // Pista debajo de la rueda
        PlaceCentered(new Border
        {
            Background = HubBrush, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 5, 12, 5),
            Child = new TextBlock
            {
                Text = _wheel == Wheel.Convert ? "Ctrl → Herramientas" : "Shift → Convertir",
                FontSize = 11, Foreground = MutedBrush,
            },
        }, new Point(0, OuterR + 24));

        WheelHost.Visibility = Visibility.Visible;
        AnimateIn();
    }

    private void AnimateIn()
    {
        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(0.75, 0.75);
        WheelHost.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.75, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.75, 1, duration) { EasingFunction = ease });
        WheelHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private static void Paint(Sector s, bool hot)
    {
        s.Shape.Fill = hot ? AccentBrush : SectorBrush;
        s.Icon.Foreground = hot ? DarkText : AccentBrush;
        s.Label.Foreground = hot ? DarkText : TextBrush;
    }

    /// <summary>Corona circular entre dos ángulos (0° = arriba, sentido horario).</summary>
    private static Geometry Wedge(double fromDeg, double toDeg)
    {
        var large = toDeg - fromDeg > 180;
        var figure = new PathFigure { StartPoint = Polar(OuterR, fromDeg), IsClosed = true };
        figure.Segments.Add(new ArcSegment(Polar(OuterR, toDeg), new Size(OuterR, OuterR), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(InnerR, toDeg), true));
        figure.Segments.Add(new ArcSegment(Polar(InnerR, fromDeg), new Size(InnerR, InnerR), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point Polar(double radius, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(radius * Math.Sin(rad), -radius * Math.Cos(rad));
    }

    private void PlaceCentered(FrameworkElement element, Point at)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(element, at.X - element.DesiredSize.Width / 2);
        Canvas.SetTop(element, at.Y - element.DesiredSize.Height / 2);
        WheelHost.Children.Add(element);
    }

    /// <summary>Evita que la rueda quede cortada en los bordes de la pantalla.</summary>
    private Point KeepOnScreen(Point p)
    {
        const double margin = OuterR + 48;
        var w = Surface.ActualWidth;
        var h = Surface.ActualHeight;
        return new Point(
            Math.Clamp(p.X, margin, Math.Max(margin, w - margin)),
            Math.Clamp(p.Y, margin, Math.Max(margin, h - margin)));
    }

    private string KindGlyph()
    {
        var kinds = _files.Select(ActionRegistry.KindOf).Distinct().ToList();
        if (kinds.Count != 1) return ""; // documento genérico
        return kinds[0] switch
        {
            FileKind.Video => "",
            FileKind.Audio => "",
            FileKind.Image or FileKind.Vector => "",
            _ => "",
        };
    }

    private static string WheelName(Wheel wheel) => wheel == Wheel.Convert ? "Convertir" : "Herramientas";

    private static void Guard(string where, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"EXCEPCIÓN en {where}: {ex}"); }
    }

    private static Brush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
