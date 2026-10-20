using Lemon.Services;

namespace Lemon.Actions;

public enum FileKind { Video, Audio, Image, Vector, Unknown }

/// <summary>Qué rueda muestra la acción: Shift = conversiones, Ctrl = herramientas.</summary>
public enum Wheel { Convert, Tools }

/// <summary>
/// Una acción = metadatos para la UI + qué herramienta usa + una función que construye sus argumentos.
/// TargetExt (si existe) sirve para ocultar "convertir a X" cuando el archivo ya es X.
/// </summary>
public sealed record FileAction(
    string Id,
    string Title,
    string Glyph,              // icono de Segoe Fluent Icons
    Wheel Wheel,
    Tool Tool,
    FileKind[] AppliesTo,
    string? TargetExt,
    Func<string, (string Output, string[] Args)> Build);

public static class ActionRegistry
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = FileKind.Video, [".mov"] = FileKind.Video, [".mkv"] = FileKind.Video,
        [".webm"] = FileKind.Video, [".avi"] = FileKind.Video,

        [".mp3"] = FileKind.Audio, [".wav"] = FileKind.Audio, [".m4a"] = FileKind.Audio, [".flac"] = FileKind.Audio,

        [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".png"] = FileKind.Image,
        [".webp"] = FileKind.Image, [".heic"] = FileKind.Image, [".heif"] = FileKind.Image,
        [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image, [".avif"] = FileKind.Image,
        [".bmp"] = FileKind.Image, [".gif"] = FileKind.Image,

        [".svg"] = FileKind.Vector,
    };

    public static FileKind KindOf(string path) =>
        Kinds.TryGetValue(Path.GetExtension(path), out var kind) ? kind : FileKind.Unknown;

    private static readonly FileKind[] Video = { FileKind.Video };
    private static readonly FileKind[] VideoAudio = { FileKind.Video, FileKind.Audio };
    private static readonly FileKind[] Raster = { FileKind.Image };
    private static readonly FileKind[] AnyImage = { FileKind.Image, FileKind.Vector };

    // Para quitar el canal alfa en formatos sin transparencia (si no, el fondo puede salir negro).
    private static readonly string[] FlattenWhite = { "-background", "white", "-alpha", "remove", "-alpha", "off" };

    /// <summary>
    /// Formatos de salida de imagen (ImageMagick). SVG queda solo como ENTRADA:
    /// convertir una foto a SVG no produce un vector real (solo incrusta la imagen).
    /// </summary>
    private static readonly (string Ext, string Title, string[] Options)[] ImageTargets =
    {
        (".jpg",  "JPG",  FlattenWhite.Concat(new[] { "-quality", "90" }).ToArray()),
        (".png",  "PNG",  Array.Empty<string>()),
        (".webp", "WebP", new[] { "-quality", "85" }),
        (".heic", "HEIC", new[] { "-quality", "80" }),
        (".tiff", "TIFF", new[] { "-compress", "lzw" }),
        (".avif", "AVIF", new[] { "-quality", "60" }),
        (".bmp",  "BMP",  FlattenWhite),
        (".gif",  "GIF",  Array.Empty<string>()),
    };

    // Para añadir una herramienta nueva solo agregas una línea aquí.
    // ext = null conserva la extensión original.
    public static readonly IReadOnlyList<FileAction> All = new List<FileAction>
    {
        // ── Shift: conversiones de video / audio (FFmpeg) ──
        Make("to-mp3", "MP3", "", Wheel.Convert, Tool.Ffmpeg, VideoAudio, ".mp3", "",
            (i, o) => new[] { "-i", i, "-vn", "-c:a", "libmp3lame", "-q:a", "2", o }),
        Make("to-wav", "WAV", "", Wheel.Convert, Tool.Ffmpeg, VideoAudio, ".wav", "",
            (i, o) => new[] { "-i", i, "-vn", o }),
        Make("to-mp4", "MP4 (H.264)", "", Wheel.Convert, Tool.Ffmpeg, Video, ".mp4", "",
            (i, o) => new[] { "-i", i, "-c:v", "libx264", "-crf", "23", "-preset", "medium",
                              "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", o }),
        Make("video-to-gif", "GIF", "", Wheel.Convert, Tool.Ffmpeg, Video, ".gif", "",
            (i, o) => new[] { "-i", i, "-vf", "fps=12,scale=480:-1:flags=lanczos", "-loop", "0", o }),
    }
    // ── Shift: conversiones de imagen (ImageMagick), una por formato de salida ──
    .Concat(ImageTargets.Select(t =>
        Make($"img-to-{t.Ext.TrimStart('.')}", t.Title, "", Wheel.Convert, Tool.Magick, AnyImage, t.Ext, "",
            (i, o) => MagickInput(i, firstFrameOnly: t.Ext != ".gif").Concat(t.Options).Append(o).ToArray())))
    .Concat(new[]
    {
        // ── Shift: imagen → SVG vectorial real en blanco y negro (ImageMagick + Vectorizer propio) ──
        Make("img-to-svg", "SVG (trazo)", "", Wheel.Convert, Tool.Trace, Raster, ".svg", "",
            (i, o) => new[] { i, o }),

        // ── Ctrl: herramientas ──
        Make("cut", "Cortar 10 s", "", Wheel.Tools, Tool.Ffmpeg, Video, null, "_corte",
            (i, o) => new[] { "-ss", "0", "-t", "10", "-i", i, "-c", "copy", o }),
        Make("compress", "Comprimir", "", Wheel.Tools, Tool.Ffmpeg, Video, ".mp4", "_comprimido",
            (i, o) => new[] { "-i", i, "-c:v", "libx264", "-crf", "28", "-preset", "fast",
                              "-c:a", "aac", "-b:a", "128k", o }),
        Make("extract", "Extraer audio", "", Wheel.Tools, Tool.Ffmpeg, Video, ".m4a", "",
            (i, o) => new[] { "-i", i, "-vn", "-c:a", "aac", "-b:a", "192k", o }),
        Make("mute", "Quitar audio", "", Wheel.Tools, Tool.Ffmpeg, Video, null, "_sin_audio",
            (i, o) => new[] { "-i", i, "-an", "-c:v", "copy", o }),
        Make("crop", "Recortar", "\uE7A8", Wheel.Tools, Tool.Crop, Raster, null, "_recortada",
            (i, o) => new[] { i, o }),
        Make("half", "Reducir 50%", "", Wheel.Tools, Tool.Magick, Raster, null, "_50",
            (i, o) => new[] { i, "-resize", "50%", o }),
    })
    .ToList();

    /// <summary>Acciones de una rueda que aplican a TODOS los archivos arrastrados.</summary>
    public static List<FileAction> ForFiles(IReadOnlyCollection<string> files, Wheel wheel)
    {
        var kinds = files.Select(KindOf).Distinct().ToList();
        if (kinds.Count == 0 || kinds.Contains(FileKind.Unknown)) return new();

        var exts = files.Select(f => NormalizeExt(Path.GetExtension(f))).Distinct().ToList();
        return All.Where(a => a.Wheel == wheel
                           && kinds.All(k => a.AppliesTo.Contains(k))
                           // ocultar "convertir a JPG" si todo lo arrastrado ya es JPG
                           && !(a.TargetExt is not null && exts.Count == 1 && exts[0] == a.TargetExt))
                  .ToList();
    }

    /// <summary>Entrada para ImageMagick con los ajustes según el formato de origen.</summary>
    private static IEnumerable<string> MagickInput(string input, bool firstFrameOnly)
    {
        var ext = NormalizeExt(Path.GetExtension(input));
        if (ext == ".svg")
            // Rasterizar el SVG a buena resolución y con fondo transparente.
            return new[] { "-density", "300", "-background", "none", input };

        // GIF animados / TIFF multipágina: [0] toma solo el primer cuadro para formatos estáticos.
        var multiFrame = ext is ".gif" or ".tiff";
        return new[] { multiFrame && firstFrameOnly ? input + "[0]" : input };
    }

    private static string NormalizeExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpeg" => ".jpg",
        ".tif" => ".tiff",
        ".heif" => ".heic",
        var e => e,
    };

    private static FileAction Make(string id, string title, string glyph, Wheel wheel, Tool tool, FileKind[] kinds,
                                   string? ext, string suffix, Func<string, string, string[]> args) =>
        new(id, title, glyph, wheel, tool, kinds, ext, input =>
        {
            var output = OutputPath(input, ext ?? Path.GetExtension(input), suffix);
            return (output, args(input, output));
        });

    /// <summary>Ruta junto al original, sin pisar archivos existentes: "foto (1).png".</summary>
    private static string OutputPath(string input, string extension, string suffix)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var path = Path.Combine(dir, name + extension);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}){extension}");
        return path;
    }
}
