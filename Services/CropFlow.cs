using System.Globalization;
using System.Windows.Media.Imaging;

namespace Lemon.Services;

/// <summary>
/// Imagen → recorte con editor (args = [entrada, salida]):
/// 1) ImageMagick lee el tamaño real (ya girado según EXIF) y genera una vista previa PNG liviana.
/// 2) Se abre CropWindow; el usuario ajusta los bordes.
/// 3) ImageMagick recorta la imagen ORIGINAL (no la vista previa), así que no se pierde calidad.
/// Si el usuario cancela se lanza OperationCanceledException, que MainWindow trata como "sin cambios".
/// </summary>
public static class CropFlow
{
    public static async Task<(bool Ok, string Log)> RunAsync(string[] args, CancellationToken ct)
    {
        var (input, output) = (args[0], args[1]);

        // El Drop todavía no terminó: mientras no retorne, el Explorador queda congelado.
        // Cedemos el hilo para que termine antes de abrir una ventana modal.
        await Task.Yield();

        var preview = Path.Combine(Path.GetTempPath(), $"lemon_{Guid.NewGuid():N}.png");
        try
        {
            var magick = ToolRunner.MagickPath;
            var infoTask = ToolRunner.RunCaptureAsync(magick,
                new[] { input + "[0]", "-auto-orient", "-format", "%w %h", "info:" },
                ToolRunner.MagickMissing, ct);
            var previewTask = ToolRunner.RunCaptureAsync(magick,
                new[] { input + "[0]", "-auto-orient", "-resize", "2400x2400>", "PNG32:" + preview },
                ToolRunner.MagickMissing, ct);

            var info = await infoTask;
            var prev = await previewTask;
            if (!info.Ok) return (false, info.Err);
            if (!prev.Ok) return (false, prev.Err);

            var parts = info.Out.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                || width < 1 || height < 1)
                return (false, "No se pudo leer el tamaño de la imagen.");

            // CacheOption.OnLoad: carga todo en memoria y suelta el archivo para poder borrarlo.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(preview);
            bitmap.EndInit();
            bitmap.Freeze();

            var window = new CropWindow(Path.GetFileName(input), bitmap, width, height);
            var accepted = window.ShowDialog() == true;
            if (!accepted || window.Result is null) throw new OperationCanceledException();

            var r = window.Result.Value;
            var crop = string.Create(CultureInfo.InvariantCulture, $"{r.Width}x{r.Height}+{r.X}+{r.Y}");
            Log.Write($"Recorte {crop} de {Path.GetFileName(input)} ({width}x{height})");

            // +repage: descarta el "lienzo virtual" que dejan algunos formatos (GIF, PNG, WebP) tras recortar.
            var result = await ToolRunner.RunCaptureAsync(magick,
                new[] { input, "-auto-orient", "-crop", crop, "+repage", output },
                ToolRunner.MagickMissing, ct);
            return (result.Ok, result.Err);
        }
        finally
        {
            try { File.Delete(preview); } catch { /* archivo temporal */ }
        }
    }
}
