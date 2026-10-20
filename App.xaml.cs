using System.Security.Principal;
using System.Windows;
using Lemon.Services;

namespace Lemon;

public partial class App : Application
{
    private const string InstanceName = "Lemon.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Una sola instancia: si ya hay una abierta (p. ej. la del arranque con Windows),
        // le pedimos que muestre su ventana y salimos.
        _mutex = new Mutex(true, InstanceName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch { /* la otra aún está iniciando */ }
            Shutdown();
            return;
        }

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Log.Write($"──── inicio · admin={admin} · args=[{string.Join(' ', e.Args)}] · log={Log.FilePath}");
        if (admin)
            Log.Write("AVISO: la app corre como administrador; el Explorador NO puede soltar archivos sobre ella.");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("ERROR UI: " + args.Exception);
            Toast.Notify(ToastKind.Error, "Lemon tuvo un error", args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("ERROR FATAL: " + args.ExceptionObject);

        base.OnStartup(e);

        try { Autostart.RemoveLegacyEntry(); } catch (Exception ex) { Log.Write("Autostart legacy: " + ex.Message); }

        // La ventana se crea pero NO se muestra: la app vive en la bandeja.
        _window = new MainWindow();
        MainWindow = _window;
        _tray = new TrayIcon(ShowWindow, ExitApp);

        // Escucha la señal de una segunda instancia.
        var signal = _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        new Thread(() =>
        {
            try { while (signal.WaitOne()) Dispatcher.InvokeAsync(ShowWindow); }
            catch (ObjectDisposedException) { /* la app se está cerrando */ }
        }) { IsBackground = true }.Start();

        if (!e.Args.Contains(Autostart.BackgroundArg))
            Toast.Notify(ToastKind.Info, "Lemon está activo",
                "Vive en la bandeja, junto al reloj. Arrastra un archivo y toca Shift o Ctrl.");
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.AllowClose();
        _window?.Close();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showSignal?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
