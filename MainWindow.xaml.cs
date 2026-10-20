using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Lemon.Actions;
using Lemon.Services;

namespace Lemon;

/// <summary>
/// Ventana de estado + vigilante global de teclas.
/// Durante un arrastre el teclado lo controla el programa de origen (Explorador), así que
/// no recibimos KeyDown. En su lugar consultamos el estado físico del teclado/mouse cada 25 ms:
/// si el botón principal está presionado y se TOCA Shift o Ctrl, mostramos la rueda.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WheelOverlay _overlay;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private bool _prevShift, _prevCtrl;
    private int _running;

    public MainWindow()
    {
        InitializeComponent();
        // La capa se crea DESPUÉS de esta ventana para que esta siga siendo la MainWindow de la app.
        Application.Current.MainWindow = this;
        _overlay = new WheelOverlay();
        _overlay.ActionChosen += async (action, files) => await RunActionAsync(action, files);
        _poll.Tick += Poll;
        _poll.Start();
        Closed += (_, _) => { _poll.Stop(); _overlay.Close(); };

        // Minimizar = esconder en la bandeja (sin botón en la barra de tareas).
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            Hide();
            WindowState = WindowState.Normal;
        };
    }

    private bool _allowClose;
    private bool _trayHintShown;

    /// <summary>Lo llama "Salir" del menú de la bandeja; cualquier otro cierre solo esconde la ventana.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // La X no cierra la app: la esconde y sigue funcionando en la bandeja.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Toast.Notify(ToastKind.Info, "Lemon sigue activo",
                    "Lo encuentras en la bandeja, junto al reloj. Para cerrarlo: clic derecho → Salir.");
            }
        }
        base.OnClosing(e);
    }

    private void Poll(object? sender, EventArgs e)
    {
        var mouseDown = Native.IsPrimaryMouseDown();
        var shift = Native.IsDown(Native.VK_SHIFT);
        var ctrl = Native.IsDown(Native.VK_CONTROL);
        var shiftTapped = shift && !_prevShift;
        var ctrlTapped = ctrl && !_prevCtrl;
        _prevShift = shift;
        _prevCtrl = ctrl;

        // Con el editor de recorte abierto, Shift/Ctrl son del editor: la rueda no debe aparecer encima.
        if (CropWindow.IsOpen) return;

        if (shiftTapped || ctrlTapped)
            Log.Write($"Poll: tecla {(shiftTapped ? "Shift" : "Ctrl")} · mouseDown={mouseDown} · capaVisible={_overlay.IsVisible}");

        if (!mouseDown)
        {
            // Se soltó el mouse sin que llegara un drag real: esconder la capa.
            if (_overlay.IsVisible && !_overlay.HasDrag) _overlay.Disarm("mouse soltado sin DragEnter");
            return;
        }

        Wheel? wheel = shiftTapped ? Wheel.Convert : ctrlTapped ? Wheel.Tools : (Wheel?)null;
        if (wheel is null) return;

        if (_overlay.IsVisible) _overlay.SwitchWheel(wheel.Value);
        else _overlay.Arm(wheel.Value);
    }

    private async Task RunActionAsync(FileAction action, string[] files)
    {
        _running++;
        var outputs = new List<string>();
        var failed = 0;
        string? lastError = null;

        for (var i = 0; i < files.Length; i++)
        {
            SetStatus($"{action.Title}… ({i + 1}/{files.Length})", Path.GetFileName(files[i]));
            var (output, args) = action.Build(files[i]);
            Log.Write($"{action.Tool}: {string.Join(' ', args)}");
            bool success;
            string log;
            try
            {
                (success, log) = await ToolRunner.RunAsync(action.Tool, args);
            }
            catch (OperationCanceledException)
            {
                // El usuario cerró el editor sin aplicar cambios: no es un error.
                Log.Write($"{action.Title}: cancelado ({Path.GetFileName(files[i])})");
                continue;
            }

            if (success)
            {
                outputs.Add(output);
            }
            else
            {
                failed++;
                lastError = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .LastOrDefault();
                if (string.IsNullOrWhiteSpace(lastError)) lastError = "Error desconocido. Revisa el log para más detalles.";
                Log.Write($"{action.Tool} falló: " + lastError);
            }
        }

        if (failed == 0 && outputs.Count == 0)
        {
            // Todo se canceló: sin notificación ni cambios.
            _running--;
            if (_running == 0) SetStatus("Cancelado", "No se hizo ningún cambio");
            return;
        }

        Notify(action, files.Length, outputs, failed, lastError);

        _running--;
        if (_running > 0) return; // hay otras tareas en curso; ellas actualizan el estado

        SetStatus(
            failed == 0 ? $"{action.Title}: listo ✓ ({outputs.Count}/{files.Length})"
                        : $"{action.Title}: terminó con errores ({outputs.Count}/{files.Length})",
            lastError ?? "Guardado junto al archivo original");
    }

    private static void Notify(FileAction action, int total, List<string> outputs, int failed, string? lastError)
    {
        if (failed == 0)
        {
            Toast.Notify(ToastKind.Success,
                $"{action.Title}: listo",
                outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} archivos convertidos",
                "Mostrar en carpeta", () => RevealInExplorer(outputs[0]));
            return;
        }

        var title = failed == total
            ? $"{action.Title}: no se pudo completar"
            : $"{action.Title}: {failed} de {total} con error";
        Toast.Notify(ToastKind.Error, title, lastError ?? "", "Ver detalles", OpenLog);
    }

    /// <summary>Abre el Explorador con el archivo seleccionado.</summary>
    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("No se pudo abrir el Explorador: " + ex.Message); }
    }

    private static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("No se pudo abrir el log: " + ex.Message); }
    }

    private void SetStatus(string title, string detail)
    {
        StatusText.Text = title;
        DetailText.Text = detail;
    }
}
