using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Lemon;

/// <summary>
/// Editor de recorte: una imagen con un marco que se ajusta arrastrando bordes, esquinas o el
/// interior, o escribiendo X / Y / Ancho / Alto. Todo el modelo está en PÍXELES de la imagen
/// original (ya girada según EXIF); la pantalla solo dibuja una escala de eso.
/// </summary>
public partial class CropWindow : Window
{
    /// <summary>True mientras hay un editor abierto: MainWindow suspende la rueda para que no se active aquí dentro.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>Recorte elegido (en píxeles de la imagen original). Null si se canceló.</summary>
    public Int32Rect? Result { get; private set; }

    private enum Grip { None, Move, New, NW, N, NE, W, E, SW, S, SE }

    private static readonly Grip[] HitOrder = { Grip.NW, Grip.NE, Grip.SW, Grip.SE, Grip.N, Grip.S, Grip.W, Grip.E };

    private static readonly Brush Accent = Frozen(0xFF, 0xFF, 0xD4, 0x3B);
    private static readonly Brush Ink = Frozen(0xFF, 0x1A, 0x1A, 0x1A);
    private static readonly Brush ChipOff = Frozen(0xFF, 0x2A, 0x2A, 0x2A);
    private static readonly Brush ChipText = Frozen(0xFF, 0xDD, 0xDD, 0xDD);

    private readonly int _imgW, _imgH;
    private double _x, _y, _w, _h;            // selección, en píxeles de la imagen
    private double? _ratio;                    // ancho/alto bloqueado (null = libre)
    private Rect _view;                        // dónde se dibuja la imagen dentro de Stage (dips)
    private double _scale = 1;                 // dips por píxel de imagen

    private readonly Dictionary<Grip, Rectangle> _handles = new();
    private readonly List<(Border Chip, TextBlock Label, double? Ratio)> _chips = new();

    private Grip _drag = Grip.None;
    private bool _dragMoved;
    private Point _dragStartStage;
    private Point _dragStartImage;
    private Rect _dragStartRect;
    private bool _updatingFields;

    public CropWindow(string fileName, BitmapSource preview, int imageWidth, int imageHeight)
    {
        InitializeComponent();
        IsOpen = true;

        _imgW = imageWidth;
        _imgH = imageHeight;
        _x = 0; _y = 0; _w = _imgW; _h = _imgH;      // selección inicial: toda la imagen

        Photo.Source = preview;
        TitleText.Text = fileName;

        BuildHandles();
        BuildRatioChips();
        SelectRatio(0);

        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) =>
        {
            // La ventana aparece justo después de soltar el archivo: la traemos al frente y con foco.
            Topmost = true;
            Activate();
            Topmost = false;
            Focus();
            LayoutAll();
        };
        Closed += (_, _) => IsOpen = false;
    }

    // ───────────────────────── Construcción ─────────────────────────

    private void BuildHandles()
    {
        foreach (var grip in HitOrder)
        {
            var handle = new Rectangle
            {
                Width = 11, Height = 11, RadiusX = 2, RadiusY = 2,
                Fill = Accent, Stroke = Ink, StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Stage.Children.Add(handle);
            _handles[grip] = handle;
        }
    }

    private void BuildRatioChips()
    {
        var options = new (string Label, double? Ratio)[]
        {
            ("Libre", null),
            ("Original", (double)_imgW / _imgH),
            ("1:1", 1.0),
            ("4:3", 4.0 / 3),
            ("3:2", 3.0 / 2),
            ("16:9", 16.0 / 9),
            ("3:4", 3.0 / 4),
            ("2:3", 2.0 / 3),
            ("9:16", 9.0 / 16),
        };

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var label = new TextBlock { Text = options[i].Label, FontSize = 12, Foreground = ChipText };
            var chip = new Border
            {
                Child = label, Background = ChipOff, Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 4),
            };
            chip.MouseLeftButtonUp += (_, _) => SelectRatio(index);
            RatioPanel.Children.Add(chip);
            _chips.Add((chip, label, options[i].Ratio));
        }
    }

    // ───────────────────────── Proporción ─────────────────────────

    private void SelectRatio(int index)
    {
        _ratio = _chips[index].Ratio;

        for (var i = 0; i < _chips.Count; i++)
        {
            var on = i == index;
            _chips[i].Chip.Background = on ? Accent : ChipOff;
            _chips[i].Label.Foreground = on ? Ink : ChipText;
            _chips[i].Label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (_ratio is double r) FitRatio(r);
        UpdateSelectionVisuals();
        UpdateFields();
    }

    /// <summary>El recorte más grande con esa proporción que cabe en la imagen, centrado donde estaba la selección.</summary>
    private void FitRatio(double r)
    {
        var cx = _x + _w / 2;
        var cy = _y + _h / 2;
        var w = (double)_imgW;
        var h = w / r;
        if (h > _imgH) { h = _imgH; w = h * r; }

        _w = w;
        _h = h;
        _x = Cl(cx - w / 2, 0, _imgW - w);
        _y = Cl(cy - h / 2, 0, _imgH - h);
    }

    // ───────────────────────── Dibujo ─────────────────────────

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutAll();

    private void LayoutAll()
    {
        if (Stage.ActualWidth < 1 || Stage.ActualHeight < 1) return;

        const double pad = 22;   // margen para que los tiradores de los bordes no se corten
        var availW = Math.Max(1, Stage.ActualWidth - 2 * pad);
        var availH = Math.Max(1, Stage.ActualHeight - 2 * pad);
        _scale = Math.Min(Math.Min(availW / _imgW, availH / _imgH), 4);

        var vw = _imgW * _scale;
        var vh = _imgH * _scale;
        _view = new Rect((Stage.ActualWidth - vw) / 2, (Stage.ActualHeight - vh) / 2, vw, vh);

        Canvas.SetLeft(Photo, _view.X);
        Canvas.SetTop(Photo, _view.Y);
        Photo.Width = vw;
        Photo.Height = vh;
        // Imágenes pequeñas ampliadas: píxeles nítidos, para recortar con precisión.
        RenderOptions.SetBitmapScalingMode(Photo, _scale > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        UpdateSelectionVisuals();
    }

    private Rect SelectionOnStage() =>
        new(_view.X + _x * _scale, _view.Y + _y * _scale, _w * _scale, _h * _scale);

    private void UpdateSelectionVisuals()
    {
        if (_view.Width <= 0 || _view.Height <= 0) return;

        var s = SelectionOnStage();
        Place(CropFrame, s.Left, s.Top, s.Width, s.Height);

        Place(ShadeTop, _view.Left, _view.Top, _view.Width, s.Top - _view.Top);
        Place(ShadeBottom, _view.Left, s.Bottom, _view.Width, _view.Bottom - s.Bottom);
        Place(ShadeLeft, _view.Left, s.Top, s.Left - _view.Left, s.Height);
        Place(ShadeRight, s.Right, s.Top, _view.Right - s.Right, s.Height);

        SetLine(GuideV1, s.Left + s.Width / 3, s.Top, s.Left + s.Width / 3, s.Bottom);
        SetLine(GuideV2, s.Left + 2 * s.Width / 3, s.Top, s.Left + 2 * s.Width / 3, s.Bottom);
        SetLine(GuideH1, s.Left, s.Top + s.Height / 3, s.Right, s.Top + s.Height / 3);
        SetLine(GuideH2, s.Left, s.Top + 2 * s.Height / 3, s.Right, s.Top + 2 * s.Height / 3);

        foreach (var (grip, handle) in _handles)
        {
            var (hx, hy) = HandleCenter(grip, s);
            Canvas.SetLeft(handle, hx - handle.Width / 2);
            Canvas.SetTop(handle, hy - handle.Height / 2);
            // Con proporción fija solo se usan las esquinas.
            handle.Visibility = IsEdge(grip) && _ratio is not null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void Place(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, w);
        element.Height = Math.Max(0, h);
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
    }

    private static (double X, double Y) HandleCenter(Grip grip, Rect s)
    {
        var cx = s.Left + s.Width / 2;
        var cy = s.Top + s.Height / 2;
        return grip switch
        {
            Grip.NW => (s.Left, s.Top),
            Grip.N => (cx, s.Top),
            Grip.NE => (s.Right, s.Top),
            Grip.W => (s.Left, cy),
            Grip.E => (s.Right, cy),
            Grip.SW => (s.Left, s.Bottom),
            Grip.S => (cx, s.Bottom),
            Grip.SE => (s.Right, s.Bottom),
            _ => (cx, cy),
        };
    }

    private static bool IsEdge(Grip grip) => grip is Grip.N or Grip.S or Grip.E or Grip.W;

    // ───────────────────────── Mouse ─────────────────────────

    private Grip HitGrip(Point p)
    {
        const double reach = 10;
        foreach (var grip in HitOrder)
        {
            var handle = _handles[grip];
            if (handle.Visibility != Visibility.Visible) continue;
            var cx = Canvas.GetLeft(handle) + handle.Width / 2;
            var cy = Canvas.GetTop(handle) + handle.Height / 2;
            if (Math.Abs(p.X - cx) <= reach && Math.Abs(p.Y - cy) <= reach) return grip;
        }

        if (SelectionOnStage().Contains(p)) return Grip.Move;
        if (_view.Contains(p)) return Grip.New;
        return Grip.None;
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.N or Grip.S => Cursors.SizeNS,
        Grip.E or Grip.W => Cursors.SizeWE,
        Grip.NE or Grip.SW => Cursors.SizeNESW,
        Grip.NW or Grip.SE => Cursors.SizeNWSE,
        Grip.Move => Cursors.SizeAll,
        Grip.New => Cursors.Cross,
        _ => Cursors.Arrow,
    };

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Stage);
        var grip = HitGrip(p);
        if (grip == Grip.None) return;

        _drag = grip;
        _dragMoved = grip != Grip.New;     // una selección nueva solo cambia tras mover unos píxeles
        _dragStartStage = p;
        _dragStartImage = ClampToImage(ToImage(p));
        _dragStartRect = new Rect(_x, _y, _w, _h);
        Stage.CaptureMouse();
        e.Handled = true;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Stage);
        if (_drag == Grip.None)
        {
            Stage.Cursor = CursorFor(HitGrip(p));
            return;
        }

        var ip = ClampToImage(ToImage(p));
        switch (_drag)
        {
            case Grip.Move:
                _x = Cl(_dragStartRect.X + (p.X - _dragStartStage.X) / _scale, 0, _imgW - _w);
                _y = Cl(_dragStartRect.Y + (p.Y - _dragStartStage.Y) / _scale, 0, _imgH - _h);
                break;

            case Grip.New:
                if (!_dragMoved)
                {
                    if (Math.Abs(p.X - _dragStartStage.X) < 4 && Math.Abs(p.Y - _dragStartStage.Y) < 4) return;
                    _dragMoved = true;
                }
                SetFromAnchor(_dragStartImage.X, _dragStartImage.Y,
                              ip.X >= _dragStartImage.X ? 1 : -1,
                              ip.Y >= _dragStartImage.Y ? 1 : -1, ip);
                break;

            default:
                Resize(_drag, ip);
                break;
        }

        UpdateSelectionVisuals();
        UpdateFields();
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Stage.ReleaseMouseCapture();

    private void Stage_LostMouseCapture(object sender, MouseEventArgs e) => _drag = Grip.None;

    private Point ToImage(Point p) => new((p.X - _view.X) / _scale, (p.Y - _view.Y) / _scale);

    private Point ClampToImage(Point p) => new(Cl(p.X, 0, _imgW), Cl(p.Y, 0, _imgH));

    /// <summary>Tamaño mínimo del recorte: unos 12 dips en pantalla, nunca más que la imagen.</summary>
    private double MinSize => Math.Min(Math.Max(1, 12 / _scale), Math.Min(_imgW, _imgH));

    private void Resize(Grip grip, Point ip)
    {
        double l = _dragStartRect.Left, t = _dragStartRect.Top, r = _dragStartRect.Right, b = _dragStartRect.Bottom;

        if (_ratio is not null)
        {
            // Con proporción fija se redimensiona desde las esquinas; el ancla es la esquina opuesta.
            switch (grip)
            {
                case Grip.NW: SetFromAnchor(r, b, -1, -1, ip); break;
                case Grip.NE: SetFromAnchor(l, b, 1, -1, ip); break;
                case Grip.SW: SetFromAnchor(r, t, -1, 1, ip); break;
                case Grip.SE: SetFromAnchor(l, t, 1, 1, ip); break;
            }
            return;
        }

        var min = MinSize;
        if (grip is Grip.W or Grip.NW or Grip.SW) l = Cl(ip.X, 0, r - min);
        if (grip is Grip.E or Grip.NE or Grip.SE) r = Cl(ip.X, l + min, _imgW);
        if (grip is Grip.N or Grip.NW or Grip.NE) t = Cl(ip.Y, 0, b - min);
        if (grip is Grip.S or Grip.SW or Grip.SE) b = Cl(ip.Y, t + min, _imgH);

        _x = l; _y = t; _w = r - l; _h = b - t;
    }

    /// <summary>
    /// Rectángulo que crece desde un punto fijo (el ancla) hacia el cursor. sx / sy indican hacia qué lado
    /// del ancla está el rectángulo. Con proporción fija el tamaño sigue al eje que más se aleja.
    /// </summary>
    private void SetFromAnchor(double ax, double ay, int sx, int sy, Point ip)
    {
        var maxW = sx > 0 ? _imgW - ax : ax;
        var maxH = sy > 0 ? _imgH - ay : ay;
        var min = MinSize;
        var w = Math.Abs(ip.X - ax);
        var h = Math.Abs(ip.Y - ay);

        if (_ratio is double r)
        {
            var cap = Math.Min(maxW, maxH * r);
            w = Math.Min(Math.Max(w, h * r), cap);
            w = Math.Max(w, Math.Min(min, cap));
            h = w / r;
        }
        else
        {
            w = Math.Min(Math.Max(w, Math.Min(min, maxW)), maxW);
            h = Math.Min(Math.Max(h, Math.Min(min, maxH)), maxH);
        }

        _w = w;
        _h = h;
        _x = sx > 0 ? ax : ax - w;
        _y = sy > 0 ? ay : ay - h;
    }

    // ───────────────────────── Campos numéricos ─────────────────────────

    private static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private void UpdateFields()
    {
        _updatingFields = true;
        XBox.Text = R(_x).ToString(CultureInfo.InvariantCulture);
        YBox.Text = R(_y).ToString(CultureInfo.InvariantCulture);
        WBox.Text = R(_w).ToString(CultureInfo.InvariantCulture);
        HBox.Text = R(_h).ToString(CultureInfo.InvariantCulture);
        _updatingFields = false;

        InfoText.Text = $"Original {_imgW} × {_imgH} px   →   recorte {R(_w)} × {R(_h)} px";
    }

    private static int ParseOr(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Aplica lo escrito en X / Y / Ancho / Alto (al presionar Enter o salir del campo).</summary>
    private void ApplyFields()
    {
        int cx = R(_x), cy = R(_y), cw = R(_w), ch = R(_h);
        int x = ParseOr(XBox, cx), y = ParseOr(YBox, cy), w = ParseOr(WBox, cw), h = ParseOr(HBox, ch);
        if (x == cx && y == cy && w == cw && h == ch) { UpdateFields(); return; }

        var widthEdited = w != cw;
        var heightEdited = h != ch;

        x = Cl(x, 0, _imgW - 1);
        y = Cl(y, 0, _imgH - 1);
        w = Cl(w, 1, _imgW - x);
        h = Cl(h, 1, _imgH - y);

        if (_ratio is double r)
        {
            if (heightEdited && !widthEdited) w = R(h * r); else h = R(w / r);
            if (w > _imgW - x) { w = _imgW - x; h = R(w / r); }
            if (h > _imgH - y) { h = _imgH - y; w = R(h * r); }
            w = Math.Max(1, w);
            h = Math.Max(1, h);
        }

        _x = x; _y = y; _w = w; _h = h;
        UpdateSelectionVisuals();
        UpdateFields();
    }

    private void Field_Commit(object sender, RoutedEventArgs e)
    {
        if (!_updatingFields) ApplyFields();
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyFields();
        ((TextBox)sender).SelectAll();
        e.Handled = true;
    }

    // ───────────────────────── Teclado y botones ─────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        // Dentro de un campo de texto, las flechas y Enter son de ese campo.
        if (Keyboard.FocusedElement is TextBox) return;

        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Enter: Accept(); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Nudge(double dx, double dy)
    {
        _x = Cl(_x + dx, 0, _imgW - _w);
        _y = Cl(_y + dy, 0, _imgH - _h);
        UpdateSelectionVisuals();
        UpdateFields();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var x = Cl(R(_x), 0, _imgW - 1);
        var y = Cl(R(_y), 0, _imgH - 1);
        var w = Cl(R(_w), 1, _imgW - x);
        var h = Cl(R(_h), 1, _imgH - y);
        Result = new Int32Rect(x, y, w, h);
        DialogResult = true;
    }

    // ───────────────────────── Utilidades ─────────────────────────

    private static double Cl(double v, double lo, double hi) => hi < lo ? lo : Math.Min(Math.Max(v, lo), hi);

    private static int Cl(int v, int lo, int hi) => hi < lo ? lo : Math.Min(Math.Max(v, lo), hi);

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { /* solo estética */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
