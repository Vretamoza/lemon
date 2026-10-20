using Microsoft.Win32;

namespace Lemon.Services;

/// <summary>
/// Arranque con Windows mediante HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// (solo para tu usuario, no requiere permisos de administrador).
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lemon";
    private const string LegacyValueName = "Tangerine" + "Win";

    /// <summary>Al arrancar con Windows la app inicia en silencio (sin notificación de bienvenida).</summary>
    public const string BackgroundArg = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArg}";

    /// <summary>True si la entrada existe y apunta a ESTE .exe.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Borra la entrada que dejó la versión anterior de la app (antes de llamarse Lemon).</summary>
    public static void RemoveLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null) return;
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        Log.Write($"Autostart: eliminada la entrada antigua '{LegacyValueName}'");
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write($"Autostart = {enabled} · {Command}");
    }
}
