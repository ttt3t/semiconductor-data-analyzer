using System.Windows;
using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Controls;

public sealed record WaferCategorySeries(string Category, Brush LineBrush, double?[] Values);

/// <summary>Compact aggregate series only; the virtualized table supplies matching wafer labels.</summary>
public sealed class WaferCategoryTrendControl : FrameworkElement
{
    public const double LabelColumnWidth = 200;
    public const double WaferColumnWidth = 120;
    private IReadOnlyList<string> _wafers = Array.Empty<string>();
    private IReadOnlyList<WaferCategorySeries> _series = Array.Empty<WaferCategorySeries>();
    private double _horizontalOffset;
    private double _columnWidth = WaferColumnWidth;
    private double _maximum = 1;
    private double _minimum;
    public bool FitYAxisToData { get; set; }
    public string AxisTitle { get; set; } = "分类占比（%）";
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(71, 85, 105)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1));
    private static readonly Pen AxisPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 1));

    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    public double ColumnWidth
    {
        get => _columnWidth;
        set
        {
            if (!double.IsFinite(value) || value < 1 || _columnWidth == value) return;
            _columnWidth = value;
            InvalidateVisual();
        }
    }
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            value = Math.Max(0, value);
            if (_horizontalOffset == value) return;
            _horizontalOffset = value;
            InvalidateVisual();
        }
    }

    public void SetData(IReadOnlyList<string> wafers, IReadOnlyList<WaferCategorySeries> series)
    {
        _wafers = wafers; _series = series;
        double maximum = series.SelectMany(s => s.Values).Where(v => v.HasValue && double.IsFinite(v.Value))
            .Select(v => v!.Value).DefaultIfEmpty(0).Max();
        double requested = maximum > 0 ? Math.Min(100, maximum * 1.08) : 1;
        var ticks = ChartAxis.Ticks(0, requested, 5);
        double step = ticks.Count > 1 ? ticks[1] - ticks[0] : 1;
        _maximum = Math.Min(100, Math.Max(step, Math.Ceiling(requested / step) * step));
        _minimum = 0;
        if (FitYAxisToData)
        {
            double minimum = series.SelectMany(s => s.Values).Where(v => v.HasValue && double.IsFinite(v.Value))
                .Select(v => v!.Value).DefaultIfEmpty(0).Min();
            _minimum = Math.Max(0, Math.Min(_maximum - 5, Math.Floor(minimum / 5) * 5));
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
        if (width < LabelColumnWidth + 40 || height < 80) return;
        // Match the frozen label column and pixel-scrolling wafer columns beneath the plot.
        var plot = new Rect(LabelColumnWidth + 1, 25,
            Math.Max(1, width - LabelColumnWidth - SystemParameters.VerticalScrollBarWidth - 2), height - 40);
        ChartAxis.DrawTitle(dc, this, AxisTitle, new Rect(14, 2, LabelColumnWidth - 20, 22));
        var coarseTicks = ChartAxis.Ticks(_minimum, _maximum, Math.Clamp((int)(plot.Height / 48), 2, 6));
        // Half-step labels make the trend easier to compare, with spacing adapting to the plot height.
        double step = (coarseTicks.Count > 1 ? coarseTicks[1] - coarseTicks[0] : _maximum - _minimum) / 2;
        double firstTick = Math.Ceiling(_minimum / step) * step;
        int intervals = Math.Min(32, (int)Math.Floor((_maximum - firstTick) / step + 1e-9));
        for (int tickIndex = 0; tickIndex <= intervals; tickIndex++)
        {
            double tick = firstTick + tickIndex * step;
            double y = plot.Bottom - plot.Height * (tick - _minimum) / (_maximum - _minimum);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = ChartAxis.Text(this, ChartAxis.Format(tick, step) + "%", 12, TextBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 12, y - label.Height / 2));
        }
        dc.DrawLine(AxisPen, plot.TopLeft, plot.BottomLeft);
        dc.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        if (_series.Count == 0 || _wafers.Count == 0)
        {
            ChartAxis.DrawTitle(dc, this, _wafers.Count == 0 ? "暂无晶圆数据" : "请在左侧勾选需要显示的分类",
                new Rect(plot.Left + 18, plot.Top + plot.Height / 2, Math.Max(1, plot.Width - 30), 30));
            return;
        }

        double X(int index) => plot.Left + (index + .5) * _columnWidth - _horizontalOffset;
        double Y(double value) => plot.Bottom - plot.Height * (value - _minimum) / (_maximum - _minimum);
        int first = Math.Max(0, (int)Math.Floor(_horizontalOffset / _columnWidth) - 1);
        int last = Math.Min(_wafers.Count - 1, (int)Math.Ceiling((_horizontalOffset + plot.Width) / _columnWidth));
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 3, plot.Width, plot.Height + 6)));
        for (int i = first; i <= last; i++)
            dc.DrawLine(GridPen, new Point(X(i), plot.Top), new Point(X(i), plot.Bottom));
        bool hasData = false;
        foreach (var series in _series)
        {
            var line = new StreamGeometry();
            using (var context = line.Open())
            {
                bool connected = false;
                for (int i = first; i <= last; i++)
                {
                    if (i >= series.Values.Length || series.Values[i] is not double value || !double.IsFinite(value))
                    { connected = false; continue; } // Unknown denominator: leave a gap, never invent a zero.
                    hasData = true;
                    var point = new Point(X(i), Y(value));
                    if (!connected) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                    connected = true;
                }
            }
            line.Freeze();
            var pen = new Pen(series.LineBrush, 2); pen.Freeze();
            dc.DrawGeometry(null, pen, line);
            for (int i = first; i <= last && i < series.Values.Length; i++)
                if (series.Values[i] is double value && double.IsFinite(value))
                    dc.DrawEllipse(series.LineBrush, null, new Point(X(i), Y(value)), 3, 3);
        }
        dc.Pop();
        if (!hasData)
            ChartAxis.DrawTitle(dc, this, "当前晶圆无有效 SBIN 数据",
                new Rect(plot.Left + 18, plot.Top + plot.Height / 2, Math.Max(1, plot.Width - 30), 30));
    }
}
