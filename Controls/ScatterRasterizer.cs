using System.Runtime.CompilerServices;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

internal sealed record ScatterRasterRequest(ScatterSeries Series, PagedVector<byte>? Marks, bool[] SmallMarks,
    int Width, int Height, double Dpi, double PlotWidth, double PlotHeight, double DataMinX, double DataMaxX,
    double DataMinY, double DataMaxY, double ScaleX, double ScaleY, double OffsetX, double OffsetY,
    double VisibleMinX, double VisibleMaxX, bool Grouped);

/// <summary>The original point stamps and red/green priority passes, fed by paged data on a worker.</summary>
internal sealed class ScatterRasterizer
{
    private static readonly Color PointColor = Color.FromRgb(30, 120, 200);
    private static readonly Color GroupDarkColor = Color.FromRgb(31, 95, 165);
    private static readonly Color GroupLightColor = Color.FromRgb(128, 185, 231);
    private static readonly Color MarkDarkColor = Color.FromRgb(22, 125, 62);
    private static readonly Color MarkLightColor = Color.FromRgb(116, 209, 144);
    private static readonly Color OutColor = Color.FromRgb(220, 60, 60);
    private const double PointRadius = 1.35;
    private double _stampDpi;
    private int _stampRadius;
    private byte[] _stampAlpha = Array.Empty<byte>();
    private uint[] _stampNormal = Array.Empty<uint>(), _stampDark = Array.Empty<uint>(), _stampLight = Array.Empty<uint>(), _stampOut = Array.Empty<uint>(), _stampMarkDark = Array.Empty<uint>(), _stampMarkLight = Array.Empty<uint>();
    private uint[] _pixelBuffer = Array.Empty<uint>();
    public uint[] Render(ScatterRasterRequest request, CancellationToken token)
    {
        var _series = request.Series;
        var points = _series.Points;
        int bmpW = request.Width, bmpH = request.Height;
        double _plotW = request.PlotWidth, _plotH = request.PlotHeight;
        double _dataMinX = request.DataMinX, _dataMaxX = request.DataMaxX, _dataMinY = request.DataMinY, _dataMaxY = request.DataMaxY;
        double _scaleX = request.ScaleX, _scaleY = request.ScaleY, _offsetX = request.OffsetX, _offsetY = request.OffsetY;
        double visMinX = request.VisibleMinX, visMaxX = request.VisibleMaxX;
        bool grouped = request.Grouped;
        _pixelBuffer = new uint[bmpW * bmpH]; var _occupancy = new byte[bmpW * bmpH];
        using var _failCoords = new PagedVector<(int X, int Y)>();
        using var _markedCoords = new PagedVector<(int X, int Y, bool Light)>();
        EnsureStamp(request.Dpi);
        double pxPerDipX = bmpW / _plotW;
        double pxPerDipY = bmpH / _plotH;

        double scaleX, biasX;
        if (_dataMaxX == _dataMinX)
        {
            scaleX = 0;
            biasX = (0.5 * _plotW * _scaleX + _offsetX) * pxPerDipX;
        }
        else
        {
            double invSpanX = 1.0 / (_dataMaxX - _dataMinX);
            scaleX = (_plotW * _scaleX * invSpanX) * pxPerDipX;
            biasX = (_offsetX - _dataMinX * _plotW * _scaleX * invSpanX) * pxPerDipX;
        }

        double scaleY, biasY;
        if (_dataMaxY == _dataMinY)
        {
            scaleY = 0;
            biasY = (_plotH - (0.5 * _plotH * _scaleY + _offsetY)) * pxPerDipY;
        }
        else
        {
            double invSpanY = 1.0 / (_dataMaxY - _dataMinY);
            scaleY = -(_plotH * _scaleY * invSpanY) * pxPerDipY;
            biasY = (_plotH - _offsetY + _dataMinY * _plotH * _scaleY * invSpanY) * pxPerDipY;
        }

        int r = _stampRadius;
        int start = ScatterSeries.LowerBound(points, visMinX - 1);
        var groups = _series.Groups;
        int groupIndex = grouped && start < points.Length ? ScatterSeries.LowerBoundGroup(groups, points[start].Index) : 0;

        for (int i = start; i < points.Length && points[i].Index <= visMaxX + 1; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var point = points[i];
            int ix = (int)Math.Round(Math.FusedMultiplyAdd(point.Index, scaleX, biasX));
            int iy = (int)Math.Round(Math.FusedMultiplyAdd(point.Value, scaleY, biasY));
            if (ix < -r || ix >= bmpW + r || iy < -r || iy >= bmpH + r) continue;

            if (grouped)
                while (groupIndex + 1 < groups.Length && point.Index > groups[groupIndex].EndIndex) groupIndex++;

            // A clipped point outside the image must not occupy the center of an inside point.
            bool centerInside = (uint)ix < (uint)bmpW && (uint)iy < (uint)bmpH;
            int centerIdx = centerInside ? iy * bmpW + ix : -1;

            if (request.Marks != null ? request.Marks[i] != 0 : request.SmallMarks.Length > 0 && request.SmallMarks[i])
            {
                bool light = grouped && groupIndex % 2 != 0;
                if (!centerInside || _occupancy[centerIdx] < 4)
                {
                    if (centerInside) _occupancy[centerIdx] = light ? (byte)5 : (byte)4;
                    _markedCoords.Add((ix, iy, light));
                }
                continue;
            }

            if (point.OutOfSpec)
            {
                if (!centerInside || _occupancy[centerIdx] < 3)
                {
                    if (centerInside) _occupancy[centerIdx] = 3;
                    _failCoords.Add((ix, iy));
                }
                continue;
            }

            byte state = !grouped ? (byte)1 : (groupIndex % 2 == 0 ? (byte)1 : (byte)2);
            if (centerInside && _occupancy[centerIdx] != 0) continue;
            if (centerInside) _occupancy[centerIdx] = state;

            uint[] stamp = !grouped ? _stampNormal : (state == 1 ? _stampDark : _stampLight);
            StampCircle(bmpW, bmpH, ix, iy, r, stamp);
        }

        // Second pass: stamp out-of-spec red points on top so failures are never hidden behind pass points.
        for (int i = 0; i < _failCoords.Count; i++)
        {
            var (ix, iy) = _failCoords[i];
            StampCircle(bmpW, bmpH, ix, iy, r, _stampOut);
        }

        // Marking is a display override, including failed points; specifications/statistics stay intact.
        foreach (var (ix, iy, light) in _markedCoords)
            StampCircle(bmpW, bmpH, ix, iy, r, light ? _stampMarkLight : _stampMarkDark);

        return _pixelBuffer;
    }
    private void EnsureStamp(double dpi)
    {
        if (_stampDpi == dpi && _stampAlpha.Length > 0) return;
        _stampDpi = dpi;
        double radius = PointRadius * dpi;
        int r = Math.Max(1, (int)Math.Ceiling(radius + 0.5));
        int size = r * 2 + 1;
        int len = size * size;
        _stampRadius = r;
        _stampAlpha = new byte[len];
        _stampNormal = new uint[len];
        _stampDark = new uint[len];
        _stampLight = new uint[len];
        _stampOut = new uint[len];
        _stampMarkDark = new uint[len];
        _stampMarkLight = new uint[len];

        static uint Premultiply(Color c, byte a)
        {
            if (a == 0) return 0;
            uint pr = (uint)((c.R * a + 127) / 255);
            uint pg = (uint)((c.G * a + 127) / 255);
            uint pb = (uint)((c.B * a + 127) / 255);
            return ((uint)a << 24) | (pr << 16) | (pg << 8) | pb;
        }

        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double coverage = Math.Clamp(radius + 0.5 - dist, 0.0, 1.0);
                byte alpha = (byte)Math.Round(coverage * 255.0);
                int idx = (dy + r) * size + (dx + r);
                _stampAlpha[idx] = alpha;
                _stampNormal[idx] = Premultiply(PointColor, alpha);
                _stampDark[idx] = Premultiply(GroupDarkColor, alpha);
                _stampLight[idx] = Premultiply(GroupLightColor, alpha);
                _stampOut[idx] = Premultiply(OutColor, alpha);
                _stampMarkDark[idx] = Premultiply(MarkDarkColor, alpha);
                _stampMarkLight[idx] = Premultiply(MarkLightColor, alpha);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StampCircle(int bmpW, int bmpH, int cx, int cy, int r, uint[] stampColors)
    {
        int size = r * 2 + 1;
        var alphas = _stampAlpha;
        var pixels = _pixelBuffer;

        // Interior fast path: zero per-pixel bounds checks.
        if (cx >= r && cx < bmpW - r && cy >= r && cy < bmpH - r)
        {
            int stampIdx = 0;
            for (int dy = -r; dy <= r; dy++)
            {
                int rowOffset = (cy + dy) * bmpW + cx;
                for (int dx = -r; dx <= r; dx++, stampIdx++)
                {
                    byte a = alphas[stampIdx];
                    if (a == 0) continue;
                    int idx = rowOffset + dx;
                    uint src = stampColors[stampIdx];
                    uint dst = pixels[idx];
                    pixels[idx] = (dst == 0 || a == 255) ? src : BlendPremultiplied(src, a, dst);
                }
            }
            return;
        }

        int dyMin = Math.Max(-r, -cy);
        int dyMax = Math.Min(r, bmpH - 1 - cy);
        int dxMin = Math.Max(-r, -cx);
        int dxMax = Math.Min(r, bmpW - 1 - cx);

        for (int dy = dyMin; dy <= dyMax; dy++)
        {
            int rowOffset = (cy + dy) * bmpW + cx;
            int stampRow = (dy + r) * size + r;
            for (int dx = dxMin; dx <= dxMax; dx++)
            {
                int stampIdx = stampRow + dx;
                byte a = alphas[stampIdx];
                if (a == 0) continue;
                int idx = rowOffset + dx;
                uint src = stampColors[stampIdx];
                uint dst = pixels[idx];
                pixels[idx] = (dst == 0 || a == 255) ? src : BlendPremultiplied(src, a, dst);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BlendPremultiplied(uint src, byte srcA, uint dst)
    {
        uint invA = (uint)(255 - srcA);
        uint b = (src & 0xFF) + (((dst & 0xFF) * invA + 127) >> 8);
        uint g = ((src >> 8) & 0xFF) + ((((dst >> 8) & 0xFF) * invA + 127) >> 8);
        uint r = ((src >> 16) & 0xFF) + ((((dst >> 16) & 0xFF) * invA + 127) >> 8);
        uint a = (src >> 24) + (((dst >> 24) * invA + 127) >> 8);
        if (b > 255) b = 255;
        if (g > 255) g = 255;
        if (r > 255) r = 255;
        if (a > 255) a = 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

}
