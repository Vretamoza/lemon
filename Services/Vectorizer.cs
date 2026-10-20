using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lemon.Services;

/// <summary>
/// Trazador bitmap → SVG propio (reemplaza a Potrace, sin dependencias externas).
///
/// 1. Lee una imagen en blanco y negro y marca los píxeles negros.
/// 2. Extrae los contornos siguiendo los bordes entre píxeles negros y blancos (bucles cerrados;
///    los huecos salen como bucles propios y se resuelven con fill-rule="evenodd").
/// 3. Simplifica cada contorno con Douglas-Peucker (convierte las "escaleras" de píxeles en rectas).
/// 4. Suaviza con curvas Bézier cuadráticas, conservando como esquina los giros bruscos.
/// </summary>
public static class Vectorizer
{
    private const double Tolerance = 1.0;   // desviación máxima en px al simplificar
    private const double CornerAngle = 55;  // giro (grados) a partir del cual un vértice se deja como esquina
    private const double MinArea = 4;       // ignora manchas de menos de 4 px²

    public static void TraceToSvg(string bitmapPath, string svgPath)
    {
        var (black, w, h) = LoadMask(bitmapPath);
        var d = new StringBuilder();

        foreach (var loop in ExtractLoops(black, w, h))
        {
            if (Math.Abs(Area(loop)) < MinArea) continue;
            var simplified = SimplifyClosed(loop, Tolerance);
            if (simplified.Count >= 3) AppendSmoothPath(d, simplified);
        }

        File.WriteAllText(svgPath,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\">\n" +
            $"  <path fill=\"#000000\" fill-rule=\"evenodd\" d=\"{d}\"/>\n" +
            "</svg>\n");
    }

    // ───────────────────────── 1. Máscara ─────────────────────────

    private static (bool[] Black, int W, int H) LoadMask(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);

        int w = gray.PixelWidth, h = gray.PixelHeight;
        var pixels = new byte[w * h];
        gray.CopyPixels(pixels, w, 0);

        var black = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) black[i] = pixels[i] < 128;
        return (black, w, h);
    }

    // ───────────────────────── 2. Contornos ─────────────────────────

    private static List<List<Point>> ExtractLoops(bool[] black, int w, int h)
    {
        // Vértices = esquinas de píxel (w+1 × h+1). Cada vértice tiene como máximo 2 aristas de salida.
        var vw = w + 1;
        var count = vw * (h + 1);
        var out1 = new int[count];
        var out2 = new int[count];
        Array.Fill(out1, -1);
        Array.Fill(out2, -1);

        bool IsBlack(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && black[y * w + x];

        void AddEdge(int fx, int fy, int tx, int ty)
        {
            int from = fy * vw + fx, to = ty * vw + tx;
            if (out1[from] < 0) out1[from] = to; else out2[from] = to;
        }

        // Aristas orientadas en sentido horario alrededor de cada región negra.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!IsBlack(x, y)) continue;
            if (!IsBlack(x, y - 1)) AddEdge(x, y, x + 1, y);             // arriba
            if (!IsBlack(x + 1, y)) AddEdge(x + 1, y, x + 1, y + 1);     // derecha
            if (!IsBlack(x, y + 1)) AddEdge(x + 1, y + 1, x, y + 1);     // abajo
            if (!IsBlack(x - 1, y)) AddEdge(x, y + 1, x, y);             // izquierda
        }

        // Cada vértice tiene tantas entradas como salidas, así que caminar desde un vértice
        // siempre regresa a él: cada caminata es un bucle cerrado.
        var loops = new List<List<Point>>();
        var corners = new List<(int X, int Y)>();
        for (var start = 0; start < count; start++)
        {
            while (out1[start] >= 0 || out2[start] >= 0)
            {
                corners.Clear();
                var cur = start;
                do
                {
                    int next;
                    if (out1[cur] >= 0) { next = out1[cur]; out1[cur] = -1; }
                    else { next = out2[cur]; out2[cur] = -1; }
                    corners.Add((cur % vw, cur / vw));
                    cur = next;
                } while (cur != start);

                loops.Add(RemoveCollinear(corners));
            }
        }
        return loops;
    }

    private static List<Point> RemoveCollinear(List<(int X, int Y)> pts)
    {
        var result = new List<Point>();
        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var a = pts[(i - 1 + n) % n];
            var b = pts[i];
            var c = pts[(i + 1) % n];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross != 0) result.Add(new Point(b.X, b.Y));
        }
        return result;
    }

    private static double Area(List<Point> p)
    {
        double sum = 0;
        for (int i = 0, n = p.Count; i < n; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % n];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    // ───────────────────────── 3. Simplificación ─────────────────────────

    private static List<Point> SimplifyClosed(List<Point> pts, double tolerance)
    {
        var n = pts.Count;
        if (n < 4) return pts;

        // Partimos el bucle en dos tramos: del punto 0 al más lejano, y de vuelta.
        var far = 0;
        var best = -1.0;
        for (var i = 1; i < n; i++)
        {
            var dist = (pts[i] - pts[0]).LengthSquared;
            if (dist > best) { best = dist; far = i; }
        }

        var keep = new bool[n];
        keep[0] = keep[far] = true;

        // Douglas-Peucker iterativo (sin recursión, para contornos muy largos).
        // El índice n representa el punto 0 al cerrar el bucle.
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, far));
        stack.Push((far, n));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var a = pts[first];
            var b = pts[last % n];
            var index = -1;
            var maxDist = tolerance;
            for (var i = first + 1; i < last; i++)
            {
                var dist = DistanceToSegment(pts[i], a, b);
                if (dist > maxDist) { maxDist = dist; index = i; }
            }
            if (index < 0) continue;
            keep[index] = true;
            stack.Push((first, index));
            stack.Push((index, last));
        }

        var result = new List<Point>();
        for (var i = 0; i < n; i++)
            if (keep[i]) result.Add(pts[i]);
        return result;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared;
        if (lengthSq == 0) return (p - a).Length;
        var t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSq, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    // ───────────────────────── 4. Suavizado → SVG ─────────────────────────

    private static void AppendSmoothPath(StringBuilder d, List<Point> p)
    {
        var n = p.Count;
        Point Mid(int i)
        {
            var a = p[i % n];
            var b = p[(i + 1) % n];
            return new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        }

        // Cada tramo va del punto medio de un lado al punto medio del siguiente, usando el vértice
        // como punto de control (curva) o pasando por él (esquina).
        d.Append('M').Append(Format(Mid(n - 1)));
        for (var i = 0; i < n; i++)
        {
            var prev = p[(i - 1 + n) % n];
            var cur = p[i];
            var next = p[(i + 1) % n];

            if (Math.Abs(Vector.AngleBetween(cur - prev, next - cur)) >= CornerAngle)
                d.Append('L').Append(Format(cur)).Append('L').Append(Format(Mid(i)));
            else
                d.Append('Q').Append(Format(cur)).Append(' ').Append(Format(Mid(i)));
        }
        d.Append('Z');
    }

    // Siempre con punto decimal (en español el separador por defecto sería la coma y rompería el SVG).
    private static string Format(Point p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X:0.##},{p.Y:0.##}");
}
