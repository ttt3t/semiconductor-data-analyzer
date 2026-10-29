using System.Runtime.CompilerServices;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

// The point layer remains one reused Pbgra32 bitmap, with no per-point WPF visuals/geometries.
internal static class ScatterRaster
{
    internal const uint Pass = 0xff008000;
    internal const uint Fail = 0xffdc3c3c;
    internal const uint Mark = 0xff1e78c8;
    // Premultiplied, translucent green keeps dense connections behind the opaque crosses.
    internal const uint Connection = 0x50002800;

    internal static (int X, int Y)[] CrossOffsets(double dpi, out int radius)
    {
        radius = Math.Clamp((int)Math.Ceiling(2.5 * dpi), 2, 16);
        var offsets = new (int X, int Y)[radius * 4 + 1];
        int n = 0;
        for (int d = -radius; d <= radius; d++)
        {
            offsets[n++] = (d, d);
            if (d != 0) offsets[n++] = (-d, d);
        }
        return offsets;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void StampCross(uint[] pixels, int width, int height, int x, int y,
        int radius, (int X, int Y)[] offsets, uint color)
    {
        if (x >= radius && x < width - radius && y >= radius && y < height - radius)
        {
            int center = y * width + x;
            foreach (var offset in offsets) pixels[center + offset.Y * width + offset.X] = color;
        }
        else
        {
            foreach (var offset in offsets)
            {
                int px = x + offset.X, py = y + offset.Y;
                if ((uint)px < (uint)width && (uint)py < (uint)height) pixels[py * width + px] = color;
            }
        }
    }

    internal static void DrawConnections(uint[] pixels, int width, int height, ScatterSeries series,
        double visibleMinX, double visibleMaxX, double scaleX, double biasX, double scaleY, double biasY)
    {
        var points = series.Points;
        if (points.Length < 2) return;
        // Include neighbors outside the horizontal viewport so clipped lines reach its edges.
        int start = Math.Max(0, ScatterSeries.LowerBound(points, visibleMinX) - 1);
        int end = Math.Min(points.Length - 1, ScatterSeries.LowerBound(points, visibleMaxX));
        var groups = series.Groups;
        int group = groups.Length > 0 ? ScatterSeries.LowerBoundGroup(groups, points[start].Index) : 0;
        int verticalX = -1, verticalMin = 0, verticalMax = 0;
        void FlushVertical()
        {
            if (verticalX < 0) return;
            for (int y = verticalMin; y <= verticalMax; y++)
                if (y % 6 < 3) pixels[y * width + verticalX] = Connection;
            verticalX = -1;
        }

        for (int i = start + 1; i <= end; i++)
        {
            var a = points[i - 1]; var b = points[i];
            bool boundary = group < groups.Length && b.Index > groups[group].EndIndex;
            if (boundary)
                while (group < groups.Length && b.Index > groups[group].EndIndex) group++;
            // Missing measurements and different Site/Wafer groups have no implied continuity.
            if (boundary || b.Index != a.Index + 1) { FlushVertical(); continue; }
            double x0 = Math.FusedMultiplyAdd(a.Index, scaleX, biasX);
            double y0 = Math.FusedMultiplyAdd(a.Value, scaleY, biasY);
            double x1 = Math.FusedMultiplyAdd(b.Index, scaleX, biasX);
            double y1 = Math.FusedMultiplyAdd(b.Value, scaleY, biasY);
            if (!Clip(ref x0, ref y0, ref x1, ref y1, width - 1, height - 1)) { FlushVertical(); continue; }
            int ax = (int)Math.Round(x0), ay = (int)Math.Round(y0);
            int bx = (int)Math.Round(x1), by = (int)Math.Round(y1);
            if (ax == bx)
            {
                int lo = Math.Min(ay, by), hi = Math.Max(ay, by);
                if (verticalX != ax || lo > verticalMax + 1 || hi < verticalMin - 1)
                {
                    FlushVertical(); verticalX = ax; verticalMin = lo; verticalMax = hi;
                }
                else { verticalMin = Math.Min(verticalMin, lo); verticalMax = Math.Max(verticalMax, hi); }
                continue;
            }
            FlushVertical();
            // Bresenham writes one pixel across the stroke, including at high Windows DPI.
            int dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay);
            int sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, error = dx - dy;
            while (true)
            {
                if ((dx >= dy ? ax : ay) % 6 < 3) pixels[ay * width + ax] = Connection;
                if (ax == bx && ay == by) break;
                int twice = error * 2;
                if (twice > -dy) { error -= dy; ax += sx; }
                if (twice < dx) { error += dx; ay += sy; }
            }
        }
        FlushVertical();
    }

    // Clip before integer conversion/stepping: distant offscreen samples never create huge loops.
    private static bool Clip(ref double x0, ref double y0, ref double x1, ref double y1, int maxX, int maxY)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1)) return false;
        if (Math.Max(x0, x1) < 0 || Math.Min(x0, x1) > maxX ||
            Math.Max(y0, y1) < 0 || Math.Min(y0, y1) > maxY) return false;
        if (x0 >= 0 && x0 <= maxX && x1 >= 0 && x1 <= maxX &&
            y0 >= 0 && y0 <= maxY && y1 >= 0 && y1 <= maxY) return true;
        double dx = x1 - x0, dy = y1 - y0, first = 0, last = 1;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return false;
        static bool Edge(double p, double q, ref double first, ref double last)
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > last) return false; first = Math.Max(first, t); }
            else { if (t < first) return false; last = Math.Min(last, t); }
            return true;
        }
        if (!Edge(-dx, x0, ref first, ref last) || !Edge(dx, maxX - x0, ref first, ref last) ||
            !Edge(-dy, y0, ref first, ref last) || !Edge(dy, maxY - y0, ref first, ref last)) return false;
        x1 = Math.Clamp(Math.FusedMultiplyAdd(last, dx, x0), 0, maxX);
        y1 = Math.Clamp(Math.FusedMultiplyAdd(last, dy, y0), 0, maxY);
        x0 = Math.Clamp(Math.FusedMultiplyAdd(first, dx, x0), 0, maxX);
        y0 = Math.Clamp(Math.FusedMultiplyAdd(first, dy, y0), 0, maxY);
        return true;
    }
}
