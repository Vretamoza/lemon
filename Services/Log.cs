namespace Lemon.Services;

/// <summary>Log de diagnóstico en %TEMP%\lemon.log</summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "lemon.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* el log nunca debe romper la app */ }
    }
}
