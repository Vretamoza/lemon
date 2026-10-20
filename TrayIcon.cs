using System.Diagnostics;
using System.Runtime.InteropServices;
using Lemon.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Lemon;

/// <summary>Ícono junto al reloj con menú oscuro: mostrar ventana, iniciar con Windows, log y salir.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly Drawing.Color LemonYellow = Drawing.Color.FromArgb(0xFF, 0xD4, 0x3B);

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _autostartItem;
    private bool _disposed;

    public TrayIcon(Action showWindow, Action exit)
    {
        _autostartItem = new Forms.ToolStripMenuItem("Iniciar con Windows");
        _autostartItem.Click += (_, _) => ToggleAutostart();

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            Font = new Drawing.Font("Segoe UI", 9.5f),
            ShowCheckMargin = true,
            ShowImageMargin = false,
        };
        menu.Items.Add(new Forms.ToolStripMenuItem("Lemon") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Mostrar ventana", null, (_, _) => showWindow());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Abrir log", null, (_, _) => OpenLog());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => exit());
        menu.Opening += (_, _) => _autostartItem.Checked = Autostart.IsEnabled;

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Lemon · toca Shift o Ctrl mientras arrastras",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) showWindow(); };
    }

    private void ToggleAutostart()
    {
        try
        {
            var enable = !Autostart.IsEnabled;
            Autostart.Set(enable);
            Toast.Notify(ToastKind.Info,
                enable ? "Iniciará con Windows" : "Ya no iniciará con Windows",
                enable ? "Lemon se abrirá en la bandeja cada vez que inicies sesión."
                       : "Puedes volver a activarlo desde este mismo menú.");
        }
        catch (Exception ex)
        {
            Log.Write("Autostart error: " + ex);
            Toast.Notify(ToastKind.Error, "No se pudo cambiar el inicio con Windows", ex.Message);
        }
        _autostartItem.Checked = Autostart.IsEnabled;
    }

    private static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("No se pudo abrir el log: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;   // sin esto el ícono "fantasma" queda hasta pasar el mouse
        _icon.Dispose();
    }

    // ───────────────────────── Ícono dibujado por código (limón) ─────────────────────────

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var fruit = new Drawing.SolidBrush(LemonYellow);
            using var leaf = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x3F, 0xB9, 0x50));
            g.FillEllipse(fruit, 3, 9, 26, 19);    // cuerpo, más ancho que alto
            g.FillEllipse(fruit, 0, 15, 6, 7);     // punta izquierda
            g.FillEllipse(fruit, 26, 15, 6, 7);    // punta derecha
            g.TranslateTransform(20, 6);
            g.RotateTransform(-30);
            g.FillEllipse(leaf, -6, -3, 12, 6);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    // ───────────────────────── Menú oscuro ─────────────────────────

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2) : LemonYellow;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(LemonYellow);
            using var font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold);
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            e.Graphics.DrawString("✓", font, brush, e.ImageRectangle, format);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x20, 0x20, 0x20);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x33, 0x33, 0x33);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A);

        public override Drawing.Color ToolStripDropDownBackground => Back;
        public override Drawing.Color ImageMarginGradientBegin => Back;
        public override Drawing.Color ImageMarginGradientMiddle => Back;
        public override Drawing.Color ImageMarginGradientEnd => Back;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Back;
        public override Drawing.Color CheckBackground => Back;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
    }
}
