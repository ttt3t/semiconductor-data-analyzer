using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Controls;

public class WaferMapControl : FrameworkElement
{
    public static readonly DependencyProperty DiesProperty =
        DependencyProperty.Register(nameof(Dies), typeof(IList<WaferDie>), typeof(WaferMapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((WaferMapControl)d).RebuildHoverIndex()));

    private readonly Dictionary<(int X, int Y), WaferDie> _hoverIndex = new();
    // The window displays this inside its existing visual tree; no ToolTip/Popup HWND.
    public event Action<string, Point>? HoverChanged;
    private WaferDie? _hoveredDie;
    private Rect _dieBounds = Rect.Empty;
    private double _cell;
    private int _minX, _minY, _maxX, _maxY;
    private DrawingGroup? _fills, _numbers;
    private BitmapSource? _fillBitmap;
    private StreamGeometry? _gridLines, _siteLines;
    private double _numberCell, _numberDpi;

    public WaferMapControl()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        MouseMove += (_, e) => UpdateHover(e.GetPosition(this));
        MouseLeave += (_, _) => CloseHover();
        Unloaded += (_, _) => CloseHover();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseHover(); };
        SizeChanged += (_, _) => CloseHover();
    }

    private void UpdateHover(Point point)
    {
        var die = DieAt(point);
        if (ReferenceEquals(die, _hoveredDie)) return;
        _hoveredDie = die;
        HoverChanged?.Invoke(die == null ? "" : HoverText(die), point);
    }

    private void CloseHover()
    {
        if (_hoveredDie == null) return;
        _hoveredDie = null;
        HoverChanged?.Invoke("", default);
    }

    public void ClearHover() => CloseHover();

    private void RebuildHoverIndex()
    {
        CloseHover();
        _dieBounds = Rect.Empty;
        _hoverIndex.Clear();
        _fills = _numbers = null;
        _fillBitmap = null;
        _gridLines = _siteLines = null;
        if (Dies != null)
            foreach (var die in Dies) _hoverIndex[(die.X, die.Y)] = die;
        if (_hoverIndex.Count == 0) return;
        _minX = _hoverIndex.Values.Min(d => d.X);
        _maxX = _hoverIndex.Values.Max(d => d.X);
        _minY = _hoverIndex.Values.Min(d => d.Y);
        _maxY = _hoverIndex.Values.Max(d => d.Y);
    }

    private WaferDie? DieAt(Point point)
    {
        if (_cell <= 0 || _dieBounds.IsEmpty || !_dieBounds.Contains(point) ||
            point.X >= _dieBounds.Right || point.Y >= _dieBounds.Bottom) return null;
        long x = _minX + (long)((point.X - _dieBounds.Left) / _cell);
        long y = _minY + (long)((point.Y - _dieBounds.Top) / _cell);
        return x <= _maxX && y <= _maxY ? _hoverIndex.GetValueOrDefault(((int)x, (int)y)) : null;
    }

    private static string HoverText(WaferDie die)
    {
        static string Text(string text) => string.IsNullOrWhiteSpace(text) ? "-" : text.Length > 160 ? text[..160] + "…" : text;
        string value = die.Value.HasValue ? die.Value.Value.ToString("G10", CultureInfo.InvariantCulture) : Text(die.RawValue);
        return $"Wafer：{Text(die.WaferId)}\nX：{die.X}    Y：{die.Y}\nSite：{Text(die.Site)}\n" +
            $"序列号：{Text(die.SerialNumber)}\nSBIN：{Text(die.SBin)}    HBIN：{Text(die.HBin)}\n" +
            $"分类：{Text(die.Category)}\n" + (die.AdditionalInfo.Length > 0 ? die.AdditionalInfo : $"测量值：{value} {die.Unit}");
    }

    public IList<WaferDie>? Dies
    {
        get => (IList<WaferDie>?)GetValue(DiesProperty);
        set => SetValue(DiesProperty, value);
    }

    public static readonly DependencyProperty ShowSiteBordersProperty =
        DependencyProperty.Register(nameof(ShowSiteBorders), typeof(bool), typeof(WaferMapControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowSiteBorders
    {
        get => (bool)GetValue(ShowSiteBordersProperty);
        set => SetValue(ShowSiteBordersProperty, value);
    }

    public static readonly DependencyProperty ShowBinNumbersProperty =
        DependencyProperty.Register(nameof(ShowBinNumbers), typeof(bool), typeof(WaferMapControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowBinNumbers
    {
        get => (bool)GetValue(ShowBinNumbersProperty);
        set => SetValue(ShowBinNumbersProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        _dieBounds = Rect.Empty;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));

        if (w < 40 || h < 40 || Dies == null || Dies.Count == 0)
            return;

        const double padLeft = 42, padRight = 14, padTop = 38, padBottom = 24;
        double plotW = w - padLeft - padRight;
        double plotH = h - padTop - padBottom;
        if (plotW < 10 || plotH < 10) return;

        int minX = _minX, maxX = _maxX, minY = _minY, maxY = _maxY;

        long nx = (long)maxX - minX + 1;
        long ny = (long)maxY - minY + 1;
        double cell = Math.Min(plotW / nx, plotH / ny);
        double ox = padLeft + (plotW - cell * nx) / 2;
        double oy = padTop + (plotH - cell * ny) / 2;
        _minX = minX; _minY = minY; _cell = cell;
        _dieBounds = new Rect(ox, oy, cell * nx, cell * ny);

        // Geometry is in die coordinates and retained only for the current snapshot.
        // Resizing changes this transform, not 12,000 individual drawing resources.
        EnsureBaseDrawing();
        if (_fillBitmap != null)
        {
            // One bitmap pixel represents exactly one die coordinate, not a sampled
            // measurement. Nearest-neighbor scaling keeps every BIN color distinct.
            dc.DrawImage(_fillBitmap, _dieBounds);
            dc.DrawRectangle(GridBrush(cell, ox, oy), null, _dieBounds);
        }
        else
        {
            dc.PushTransform(FrozenTransform(cell, ox, oy));
            dc.DrawDrawing(_fills);
            var border = new Pen(Brushes.White, Math.Max(0.3 / cell, 0.06));
            border.Freeze();
            dc.DrawGeometry(null, border, _gridLines);
            dc.Pop();
        }

        if (ShowBinNumbers)
        {
            // At small cell sizes the existing font rule scales linearly. Lay out
            // at a readable reference size once, then scale the frozen glyphs.
            double referenceCell = Math.Max(24, cell);
            double numberDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_numbers == null || _numberCell != referenceCell || _numberDpi != numberDpi)
            {
                _numbers = new DrawingGroup();
                using (var labels = _numbers.Open())
                    DrawBinNumbers(labels, _hoverIndex.Values, minX, minY, 0, 0, referenceCell);
                _numbers.Freeze();
                _numberCell = referenceCell; _numberDpi = numberDpi;
            }
            dc.PushTransform(FrozenTransform(cell / referenceCell, ox, oy));
            dc.DrawDrawing(_numbers);
            dc.Pop();
        }

        if (ShowSiteBorders)
        {
            _siteLines ??= BuildSiteBorders(_hoverIndex.Values, minX, minY);
            var sitePen = new Pen(Brushes.Black, Math.Min(0.35, Math.Clamp(cell * 0.1, 1.5, 3) / cell));
            sitePen.Freeze();
            dc.PushTransform(FrozenTransform(cell, ox, oy));
            dc.DrawGeometry(null, sitePen, _siteLines);
            dc.Pop();
        }

        var typeface = new Typeface("Segoe UI");
        const double dpi = 1.25;
        var axisPen = new Pen(Brushes.Black, 1);
        var tickPen = new Pen(Brushes.DimGray, 1);
        axisPen.Freeze();
        tickPen.Freeze();

        dc.DrawLine(axisPen, new Point(ox, oy), new Point(ox + cell * nx, oy));
        dc.DrawLine(axisPen, new Point(ox, oy), new Point(ox, oy + cell * ny));

        int stepX = TickStep(nx);
        int stepY = TickStep(ny);

        foreach (int x in TickValues(minX, maxX, stepX))
        {
            double sx = ox + ((long)x - minX) * cell + cell / 2;
            dc.DrawLine(tickPen, new Point(sx, oy), new Point(sx, oy - 4));
            var ft = new FormattedText(x.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, Brushes.Black, dpi);
            dc.DrawText(ft, new Point(sx - ft.Width / 2, oy - 6 - ft.Height));
        }

        foreach (int y in TickValues(minY, maxY, stepY))
        {
            double sy = oy + ((long)y - minY) * cell + cell / 2;
            dc.DrawLine(tickPen, new Point(ox, sy), new Point(ox - 4, sy));
            var ft = new FormattedText(y.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, Brushes.Black, dpi);
            dc.DrawText(ft, new Point(ox - 6 - ft.Width, sy - ft.Height / 2));
        }

        var ftInfo = new FormattedText($"原点:左上  X:{minX}~{maxX}  Y:{minY}~{maxY}  Dies:{Dies.Count}",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Brushes.Gray, dpi);
        dc.DrawText(ftInfo, new Point(8, h - ftInfo.Height - 4));
    }

    private static MatrixTransform FrozenTransform(double scale, double x, double y)
    {
        var transform = new MatrixTransform(scale, 0, 0, scale, x, y);
        transform.Freeze();
        return transform;
    }

    private void EnsureBaseDrawing()
    {
        if (_fills != null || _fillBitmap != null) return;
        long width = (long)_maxX - _minX + 1, height = (long)_maxY - _minY + 1;
        // Bound memory even for sparse/extreme coordinates. Non-solid fills use
        // the vector fallback below; the application's BIN/heat palettes are solid.
        if (width <= 8192 && height <= 8192 && width * height <= 4_000_000 &&
            _hoverIndex.Values.All(d => d.Color is SolidColorBrush))
        {
            int stride = (int)width * 4;
            var pixels = new byte[stride * (int)height];
            foreach (var die in _hoverIndex.Values)
            {
                var brush = (SolidColorBrush)die.Color;
                var color = brush.Color;
                int offset = (die.Y - _minY) * stride + (die.X - _minX) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R;
                pixels[offset + 3] = (byte)Math.Round(color.A * brush.Opacity);
            }
            _fillBitmap = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            _fillBitmap.Freeze();
            return;
        }
        _fills = new DrawingGroup();
        _gridLines = new StreamGeometry();
        using (var lines = _gridLines.Open())
            foreach (var die in _hoverIndex.Values)
                Rectangle(lines, (long)die.X - _minX, (long)die.Y - _minY, false);
        _gridLines.Freeze();
        using (var drawing = _fills.Open())
            foreach (var group in _hoverIndex.Values.GroupBy(d => d.Color))
            {
                var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var context = geometry.Open())
                    foreach (var die in group)
                        Rectangle(context, (long)die.X - _minX, (long)die.Y - _minY, true);
                geometry.Freeze();
                // Do not freeze a caller-owned, mutable brush as a side effect.
                var brush = group.Key.IsFrozen ? group.Key : group.Key.CloneCurrentValue();
                brush.Freeze();
                drawing.DrawGeometry(brush, null, geometry);
            }
        _fills.Freeze();

        static void Rectangle(StreamGeometryContext context, double x, double y, bool filled)
        {
            context.BeginFigure(new Point(x, y), filled, true);
            context.LineTo(new Point(x + 1, y), true, false);
            context.LineTo(new Point(x + 1, y + 1), true, false);
            context.LineTo(new Point(x, y + 1), true, false);
        }
    }

    private static DrawingBrush GridBrush(double cell, double x, double y)
    {
        double edge = Math.Min(0.5, Math.Max(0.3 / cell, 0.06) / 2);
        var tile = new DrawingGroup();
        using (var drawing = tile.Open())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, edge, 1));
            drawing.DrawRectangle(Brushes.White, null, new Rect(1 - edge, 0, edge, 1));
            drawing.DrawRectangle(Brushes.White, null, new Rect(edge, 0, 1 - edge * 2, edge));
            drawing.DrawRectangle(Brushes.White, null, new Rect(edge, 1 - edge, 1 - edge * 2, edge));
        }
        tile.Freeze();
        var brush = new DrawingBrush(tile)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(x, y, cell, cell),
            TileMode = TileMode.Tile, Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    private void DrawBinNumbers(DrawingContext dc, IEnumerable<WaferDie> dies,
        int minX, int minY, double ox, double oy, double cell)
    {
        // Reuse shaped text per BIN/color; the caller freezes the resulting drawing.
        var labels = new Dictionary<(string Text, bool Light), FormattedText>();
        var typeface = new Typeface("Segoe UI");
        double fontSize = Math.Min(12, cell * 0.5);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var die in dies)
        {
            string label = string.IsNullOrWhiteSpace(die.Category) || die.Category == "(空)" ? "-" : die.Category;
            bool light = die.Color is SolidColorBrush fill &&
                fill.Color.R * 0.299 + fill.Color.G * 0.587 + fill.Color.B * 0.114 < 150;
            if (!labels.TryGetValue((label, light), out var text))
            {
                text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, light ? Brushes.White : Brushes.Black, dpi);
                double fit = Math.Min(1, Math.Min(cell * 0.8 / Math.Max(0.01, text.WidthIncludingTrailingWhitespace),
                    cell * 0.72 / Math.Max(0.01, text.Height)));
                if (fit < 1) text.SetFontSize(fontSize * fit);
                labels[(label, light)] = text;
            }
            double x = ox + ((long)die.X - minX) * cell + (cell - text.WidthIncludingTrailingWhitespace) / 2;
            double y = oy + ((long)die.Y - minY) * cell + (cell - text.Height) / 2;
            dc.DrawText(text, new Point(x, y));
        }
    }

    private static StreamGeometry BuildSiteBorders(IEnumerable<WaferDie> dies, int minX, int minY)
    {
        var sites = new Dictionary<(long X, long Y), string>();
        foreach (var die in dies)
            sites[(die.X, die.Y)] = die.Site?.Trim() ?? "";

        var edges = new Dictionary<(bool Vertical, long Axis), List<long>>();
        void Edge(bool vertical, long axis, long start)
        {
            if (!edges.TryGetValue((vertical, axis), out var starts))
                edges[(vertical, axis)] = starts = new();
            starts.Add(start);
        }
        bool HasSite(long x, long y) => sites.TryGetValue((x, y), out var site) && site.Length > 0;
        bool SameSite(long x, long y, string site) => sites.TryGetValue((x, y), out var neighbor) && neighbor == site;
        foreach (var entry in sites)
        {
            var (gx, gy) = entry.Key;
            string site = entry.Value;
            if (site.Length == 0) continue;
            long x = gx - minX, y = gy - minY;
            // Left/top draw shared boundaries once; right/bottom close exposed edges.
            if (!SameSite(gx - 1, gy, site))
                Edge(true, x, y);
            if (!SameSite(gx, gy - 1, site))
                Edge(false, y, x);
            if (!HasSite(gx + 1, gy))
                Edge(true, x + 1, y);
            if (!HasSite(gx, gy + 1))
                Edge(false, y + 1, x);
        }
        var geometry = new StreamGeometry();
        using (var lines = geometry.Open())
        {
            // Join consecutive collinear edges without bridging holes. A typical
            // CP site stripe becomes one line instead of one line per die.
            foreach (var (key, starts) in edges)
            {
                starts.Sort();
                long start = starts[0], end = start + 1;
                void Flush()
                {
                    var from = key.Vertical ? new Point(key.Axis, start) : new Point(start, key.Axis);
                    var to = key.Vertical ? new Point(key.Axis, end) : new Point(end, key.Axis);
                    lines.BeginFigure(from, false, false);
                    lines.LineTo(to, true, false);
                }
                for (int i = 1; i < starts.Count; i++)
                {
                    if (starts[i] <= end) end = Math.Max(end, starts[i] + 1);
                    else { Flush(); start = starts[i]; end = start + 1; }
                }
                Flush();
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static int TickStep(long count)
    {
        if (count <= 16) return 1;
        if (count <= 32) return 2;
        if (count <= 80) return 5;
        if (count <= 160) return 10;
        return Math.Max(1, (int)Math.Ceiling(count / 12.0));
    }

    private static List<int> TickValues(int min, int max, int step)
    {
        var list = new List<int>();
        for (long v = min; v <= max; v += step)
            list.Add((int)v);
        if (list.Count == 0 || list[^1] != max)
            list.Add(max);
        return list;
    }
}
