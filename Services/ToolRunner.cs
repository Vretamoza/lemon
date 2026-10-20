using System.ComponentModel;
using System.Diagnostics;

namespace Lemon.Services;

/// <summary>Cómo se ejecuta una acción.</summary>
public enum Tool
{
    Ffmpeg,
    Magick,
    /// <summary>Imagen → SVG: ImageMagick la pasa a blanco y negro y Vectorizer (C#) la traza.</summary>
    Trace,
}

public static class ToolRunner
{
    /// <summary>Nombre del ejecutable si está en el PATH, o una ruta completa al .exe.</summary>
    public static string FfmpegPath { get; set; } = "ffmpeg";
    public static string MagickPath { get; set; } = FindMagick();

    /// <summary>
    /// Si el proceso se abrió antes de instalar ImageMagick, su PATH heredado no lo incluye:
    /// buscamos el instalador estándar en Program Files y, si no, dejamos "magick" (PATH).
    /// </summary>
    private static string FindMagick()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var dir = Environment.GetFolderPath(root);
            if (!Directory.Exists(dir)) continue;
            var exe = Directory.EnumerateDirectories(dir, "ImageMagick*")
                .Select(d => Path.Combine(d, "magick.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (exe is not null) return exe;
        }
        return "magick";
    }

    private const string FfmpegMissing = "No se encontró ffmpeg. Instálalo con: winget install Gyan.FFmpeg";
    private const string MagickMissing = "No se encontró ImageMagick. Instálalo con: winget install ImageMagick.ImageMagick";

    public static Task<(bool Ok, string Log)> RunAsync(Tool tool, IEnumerable<string> args, CancellationToken ct = default) =>
        tool switch
        {
            // -n: nunca sobrescribir
            Tool.Ffmpeg => RunProcessAsync(FfmpegPath, new[] { "-hide_banner", "-n" }.Concat(args), FfmpegMissing, ct),
            Tool.Magick => RunProcessAsync(MagickPath, args, MagickMissing, ct),
            Tool.Trace => TraceToSvgAsync(args.ToArray(), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

    /// <summary>
    /// Imagen → SVG vectorial (args = [entrada, salida]):
    /// 1) ImageMagick la reduce a máx. 2000 px y la pasa a blanco y negro (BMP temporal).
    /// 2) Vectorizer traza los contornos dentro de la app.
    /// Ideal para logos, firmas, íconos e ilustraciones de línea; en fotos el resultado es una silueta.
    /// </summary>
    private static async Task<(bool Ok, string Log)> TraceToSvgAsync(string[] args, CancellationToken ct)
    {
        var (input, output) = (args[0], args[1]);
        var bitmap = Path.Combine(Path.GetTempPath(), $"lemon_{Guid.NewGuid():N}.bmp");
        try
        {
            var toBitmap = await RunProcessAsync(MagickPath, new[]
            {
                input + "[0]",                                   // primer cuadro (GIF/TIFF)
                "-background", "white", "-alpha", "remove", "-alpha", "off",
                "-resize", "2000x2000>",                         // limita memoria y tiempo
                "-colorspace", "Gray", "-threshold", "50%",
                "BMP3:" + bitmap,
            }, MagickMissing, ct);
            if (!toBitmap.Ok) return toBitmap;

            await Task.Run(() => Vectorizer.TraceToSvg(bitmap, output), ct);
            return (true, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "Error al vectorizar: " + ex.Message);
        }
        finally
        {
            try { File.Delete(bitmap); } catch { /* archivo temporal */ }
        }
    }

    private static async Task<(bool Ok, string Log)> RunProcessAsync(
        string exe, IEnumerable<string> args, string missingMessage, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // ArgumentList se encarga de las comillas (rutas con espacios, tildes, etc.)
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return (false, missingMessage);
        }

        // Leer ambos streams en paralelo evita que el proceso se bloquee con el buffer lleno.
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stdout;

        return (process.ExitCode == 0, await stderr);
    }
}
