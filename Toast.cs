using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Lemon;

public enum ToastKind { Success, Error, Info }

/// <summary>
/// Notificación flotante propia (abajo a la derecha), con el mismo estilo oscuro de la rueda.
/// Se apilan, se cierran solas (éxito 5 s, info 6 s, error 9 s) y se pausan al pasar el mouse por encima.
/// </summary>
public sealed class Toast : Window
{
    private const int MaxVisible = 4;
    private const bool PlaySounds = true;

    private static readonly List<Toast> Visible = new();   // la más reciente primero
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Color LemonYellow = Color.FromRgb(0xFF, 0xD4, 0x3B);

    private readonly DispatcherTimer _timer;
    private readonly Border _card;
    private bool _closing;

    /// <summary>Muestra una notificación. Opcionalmente con un enlace de acción ("Mostrar en carpeta").</summary>
    public static void Notify(ToastKind kind, string title, string message, string? actionText = null, Action? action = null)
    {
        var toast = new Toast(kind, title, message, actionText, action);
        Visible.Insert(0, toast);
        toast.Show();
        Reflow();

        if (Visible.Count > MaxVisible) Visible[^1].Dismiss();
        if (PlaySounds && kind == ToastKind.Success) SystemSounds.Asterisk.Play();
        if (PlaySounds && kind == ToastKind.Error) SystemSounds.Hand.Play();
    }

    private Toast(ToastKind kind, string title, string message, string? actionText, Action? action)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;   // no roba el foco
        Width = 380;
        SizeToContent = SizeToContent.Height;

        var accent = kind switch
        {
            ToastKind.Success => Color.FromRgb(0x3F, 0xB9, 0x50),
            ToastKind.Error => Color.FromRgb(0xF8, 0x51, 0x49),
            _ => LemonYellow,
        };

        // Ícono en círculo
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = kind switch { ToastKind.Success => "\uE73E", ToastKind.Error => "\uE783", _ => "\uE946" }, // ✓ ⚠ ℹ
                FontFamily = Icons, FontSize = 16, Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // Textos
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = message, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 50, Margin = new Thickness(0, 2, 0, 0),
        });
        if (actionText is not null && action is not null)
        {
            var link = new TextBlock
            {
                Text = actionText, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(LemonYellow), Cursor = Cursors.Hand,
                Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            link.MouseLeftButtonUp += (_, _) => { action(); Dismiss(); };
            text.Children.Add(link);
        }

        // Botón cerrar
        var close = new TextBlock
        {
            Text = "", FontFamily = Icons, FontSize = 10, Padding = new Thickness(4),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
        };
        close.MouseLeftButtonUp += (_, _) => Dismiss();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(close);

        // Tarjeta (el margen deja espacio para la sombra)
        _card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14, 12, 10, 12),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5 },
            RenderTransform = new TranslateTransform(40, 0),
            Opacity = 0,
            Child = grid,
        };
        Content = _card;

        // Cierre automático (pausado mientras el mouse está encima)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind switch { ToastKind.Success => 5, ToastKind.Error => 9, _ => 6 }) };
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };

        Loaded += (_, _) =>
        {
            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _card.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, duration) { EasingFunction = ease });
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            _timer.Start();
        };
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            Close();
            Visible.Remove(this);
            Reflow();
        };
        _card.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Apila las notificaciones desde la esquina inferior derecha del área de trabajo.</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 4;
        foreach (var toast in Visible)
        {
            toast.UpdateLayout();
            toast.Left = area.Right - toast.Width - 4;
            toast.Top = bottom - toast.ActualHeight;
            bottom = toast.Top + 8;   // se solapan los márgenes transparentes de la sombra
        }
    }
}
