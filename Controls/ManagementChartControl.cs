using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Draws compact aggregate charts; never creates a visual per chip.</summary>
public sealed class ManagementChartControl : FrameworkElement
{
    private ManagementTrendData _data = new(Array.Empty<string>(), new());
    public string Title { get; set; } = "";
    public bool ShowLegend { get; set; }
    private double? _columnWidth, _headerWidth;
    private double _rightPadding = 19;
    public void SetColumnLayout(double? columnWidth = null, double? headerWidth = null, double rightPadding = 19)
    {
        _columnWidth = columnWidth; _headerWidth = headerWidth; _rightPadding = rightPadding; InvalidateVisual();
    }
    private double _offset;
    public double HorizontalOffset { get => _offset; set { _offset = Math.Max(0, value); InvalidateVisual(); } }
    private static readonly Brush Text = Brushes.DimGray;
    private static readonly Pen Grid = Frozen(new Pen(Brushes.Gainsboro, .5));
    private static readonly Pen Axis = Frozen(new Pen(Brushes.Gray, .65));
    private static readonly Brush[] Ranked = { Brushes.IndianRed, Brushes.Gold, Brushes.DarkOrange, Brushes.SteelBlue, Brushes.MediumPurple, Brushes.SeaGreen };
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
    public void SetData(ManagementTrendData data) { _data = data; InvalidateVisual(); }
    private void Label(DrawingContext dc, string text, double x, double y, double size = 10, Brush? brush = null, double maxWidth = double.PositiveInfinity, bool center = false, int maxLines = 2)
    {
        if (maxWidth <= 0) return;
        var label = ChartAxis.Text(this, text, size, brush ?? Text);
        if (double.IsFinite(maxWidth)) { label.MaxTextWidth = maxWidth; label.MaxLineCount = maxLines; label.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(label, new Point(center ? x - label.Width / 2 : x, y));
    }
    private static double NiceTop(double value)
    {
        if (value <= 0 || !double.IsFinite(value)) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double step = magnitude / 2;
        return Math.Ceiling(value / step) * step;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (w <= 0 || h < 100) return;
        Label(dc, Title, w / 2, 2, 12, Brushes.DimGray, w - 24, true);
        double top = Title.Length == 0 ? 18 : 28;
        if (ShowLegend)
        {
            double x = 58;
            foreach (var series in _data.Series)
            {
                if (x + 145 > w - 15) { x = 58; top += 17; }
                dc.DrawRectangle(series.LineBrush, null, new Rect(x, top + 4, 10, 6));
                Label(dc, series.Category, x + 14, top, 9, maxWidth: 130); x += 150;
            }
            top += _data.Series.Count == 0 ? 0 : 21;
        }
        if (_data.Labels.Length == 0 || _data.Series.Count == 0)
        {
            Label(dc, "暂无可用的逐颗统计数据", w / 2, h / 2, 12, center: true); return;
        }
        if (_data.Mode == ManagementChartMode.HorizontalBars) { Horizontal(dc, w, h, top); return; }
        bool counts = _data.Mode == ManagementChartMode.StackedCounts;
        bool stacked = _data.IsStacked;
        bool pareto = _data.Mode == ManagementChartMode.Pareto;
        bool yield = _data.Mode is ManagementChartMode.YieldBars or ManagementChartMode.RadialLines or ManagementChartMode.CenterEdge;
        var values = _data.Series.SelectMany(s => s.Values).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        if (values.Length == 0) { Label(dc, "当前范围无有效样本数", w / 2, h / 2, 12, center: true); return; }
        double min = yield ? Math.Max(0, Math.Floor(Math.Min(values.DefaultIfEmpty(0).Min(), _data.Mode == ManagementChartMode.YieldBars ? _data.TargetYield : 100) / 5) * 5 - 5) : 0;
        double stackedMax = NiceTop(Enumerable.Range(0, _data.Labels.Length).Select(i => _data.Series.Sum(s => i < s.Values.Length ? s.Values[i] ?? 0 : 0)).DefaultIfEmpty(0).Max() * 1.15);
        double max = yield ? 100 : stacked ? counts ? stackedMax : Math.Min(100, stackedMax) : Math.Min(100, NiceTop(values.DefaultIfEmpty(0).Max() * 1.18));
        if (max <= min) min = Math.Max(0, max - 5);
        // Leave room above 100% for the cumulative and center/edge annotations.
        double annotationSpace = _data.Mode == ManagementChartMode.CenterEdge ? 24 : _data.Mode == ManagementChartMode.StackedPercentages ? 18 : pareto ? 12 : 0;
        double descriptionSpace = pareto && _data.Descriptions?.Any(s => !string.IsNullOrWhiteSpace(s)) == true ? 64 : 0;
        double left = _headerWidth ?? Math.Min(55, w * .18), right = _headerWidth.HasValue ? _rightPadding : Math.Min(pareto ? 51 : 19, w * (pareto ? .17 : .06));
        double plotWidth = Math.Max(1, w - left - right);
        if (_columnWidth is double columnWidth) plotWidth = Math.Min(plotWidth, columnWidth * _data.Labels.Length);
        var plot = new Rect(left, top + 13 + annotationSpace, plotWidth, Math.Max(1, h - top - 58 - annotationSpace - descriptionSpace));
        double Y(double value) => plot.Bottom - (value - min) / (max - min) * plot.Height;
        double slot = _columnWidth ?? plot.Width / _data.Labels.Length;
        int labelStride = pareto ? 1 : (int)Math.Clamp(Math.Ceiling(52 / slot), 1, _data.Labels.Length);
        double X(int index) => plot.Left + (index + .5) * slot - _offset;
        foreach (double tick in ChartAxis.Ticks(min, max, Math.Max(3, (int)(plot.Height / 48)), integers: counts))
        {
            double y = Y(tick); dc.DrawLine(Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, counts ? tick.ToString("N0") : tick.ToString("0.#"), plot.Left - Math.Min(25, plot.Left / 2), y - 7, 9, maxWidth: Math.Min(45, Math.Max(1, plot.Left - 6)), center: true);
        }
        Label(dc, _data.AxisTitle, plot.Left, top - 2, 9);
        dc.DrawLine(Axis, plot.TopLeft, plot.BottomLeft); dc.DrawLine(Axis, plot.BottomLeft, plot.BottomRight);
        if (pareto)
        {
            dc.DrawLine(Axis, plot.TopRight, plot.BottomRight);
            foreach (int p in new[] { 0, 20, 40, 60, 80, 100 }) Label(dc, p + "%", plot.Right + 6, plot.Bottom - p * plot.Height / 100 - 7, 9);
        }
        int first = Math.Max(0, (int)(_offset / slot) - 1), last = Math.Min(_data.Labels.Length - 1, (int)((_offset + plot.Width) / slot) + 1);
        double annotationClip = _data.Mode == ManagementChartMode.CenterEdge ? 30 : 20;
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - annotationClip, plot.Width, plot.Height + annotationClip)));
        if (_data.Mode == ManagementChartMode.RadialLines)
        {
            foreach (var series in _data.Series)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    bool connected = false;
                    for (int i = first; i <= last; i++)
                    {
                        if (i >= series.Values.Length || series.Values[i] is not double value) { connected = false; continue; }
                        var point = new Point(X(i), Y(value));
                        if (!connected) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                        connected = true;
                    }
                }
                geometry.Freeze(); dc.DrawGeometry(null, new Pen(series.LineBrush, 1.25), geometry);
                for (int i = first; i <= last && i < series.Values.Length; i++)
                    if (series.Values[i] is double value) dc.DrawEllipse(series.LineBrush, null, new Point(X(i), Y(value)), 2.3, 2.3);
            }
        }
        else for (int i = first; i <= last; i++)
        {
            double accumulated = 0, groupWidth = Math.Min(65, slot * .72);
            for (int s = 0; s < _data.Series.Count; s++)
            {
                var series = _data.Series[s]; if (i >= series.Values.Length || series.Values[i] is not double value) continue;
                double width = stacked ? groupWidth : groupWidth / _data.Series.Count;
                double x = X(i) - groupWidth / 2 + (stacked ? 0 : s * width), bottom = stacked ? Y(accumulated) : plot.Bottom;
                double y = Y(stacked ? accumulated + value : value);
                Brush brush = series.LineBrush;
                if (_data.Mode == ManagementChartMode.YieldBars && _data.Series.Count == 1)
                    brush = value < _data.TargetYield ? Brushes.IndianRed : value >= (series.Mean ?? series.Values.Where(v => v.HasValue).Average(v => v!.Value)) ? Brushes.ForestGreen : Brushes.Goldenrod;
                if (pareto && series.Category.StartsWith("HBIN")) brush = Ranked[i % Ranked.Length];
                double padding = Math.Min(.6, width * .08);
                if (bottom > y) dc.DrawRectangle(brush, null, new Rect(x + padding, y, Math.Max(0, width - padding * 2), bottom - y));
                if (stacked) accumulated += value;
                else if (width >= 28) Label(dc, value.ToString("F1", CultureInfo.InvariantCulture), x + width / 2, y - 14, 9, brush, center: true);
            }
            if (stacked && slot >= 45 && _data.Series.Any(s => i < s.Values.Length && s.Values[i].HasValue))
                Label(dc, counts ? accumulated.ToString("N0") : accumulated.ToString("F1") + "%", X(i), Y(accumulated) - 14, 9, center: true);
            if (_data.Mode == ManagementChartMode.CenterEdge && slot >= 52 && _data.Series.Count == 2 &&
                _data.Series[0].Values[i] is double center && _data.Series[1].Values[i] is double edge)
                Label(dc, (edge - center).ToString("+0.0;-0.0;0.0") + "pp", X(i), Y(Math.Max(center, edge)) - 27, 8, Brushes.IndianRed, center: true);
        }
        if (pareto)
        {
            double cumulative = _data.CumulativeStart; Point? previous = null;
            for (int i = 0; i < _data.Labels.Length; i++)
            {
                cumulative += _data.Series[0].Values[i] ?? 0;
                var point = new Point(X(i), plot.Bottom - cumulative / 100 * plot.Height);
                if (previous.HasValue) dc.DrawLine(new Pen(Brushes.SlateGray, 1.25), previous.Value, point);
                dc.DrawEllipse(Brushes.SlateGray, null, point, 2.5, 2.5);
                if (i >= first && i <= last && i % labelStride == 0) Label(dc, cumulative.ToString("F1") + "%", point.X, point.Y - 16, 8, center: true);
                previous = point;
            }
        }
        if (_data.Mode == ManagementChartMode.YieldBars && _data.Series.Count == 1)
        {
            var series = _data.Series[0]; double mean = series.Mean ?? series.Values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Average();
            var meanPen = new Pen(Brushes.SteelBlue, 1) { DashStyle = DashStyles.Dash };
            var targetPen = new Pen(Brushes.IndianRed, .8) { DashStyle = DashStyles.Dot };
            dc.DrawLine(meanPen, new Point(plot.Left, Y(mean)), new Point(plot.Right, Y(mean)));
            dc.DrawLine(targetPen, new Point(plot.Left, Y(_data.TargetYield)), new Point(plot.Right, Y(_data.TargetYield)));
            Label(dc, $"{(series.Mean.HasValue ? "加权均值" : "算术均值")} {mean:F2}%   参考 {_data.TargetYield:F1}%", plot.Right - 215, plot.Top + 2, 9, Brushes.SteelBlue, 215);
        }
        dc.Pop();
        for (int i = first; i <= last; i++)
        {
            double x = X(i); if (x < plot.Left || x > plot.Right || i % labelStride != 0) continue;
            Label(dc, _data.Labels[i], x, plot.Bottom + 5, 9, maxWidth: Math.Max(1, slot * labelStride - 3), center: true, maxLines: pareto ? 1 : 2);
            if (pareto && _data.Descriptions is { } descriptions && i < descriptions.Length)
                Label(dc, descriptions[i], x, plot.Bottom + 19, 9, maxWidth: Math.Max(1, slot - 6), center: true, maxLines: 5);
        }
    }
    private void Horizontal(DrawingContext dc, double w, double h, double top)
    {
        double left = Math.Min(Math.Clamp(w * .25, 150, 310), w * .4);
        var plot = new Rect(left, top + 8, Math.Max(1, w - left - 22), Math.Max(1, h - top - 46));
        var series = _data.Series[0]; double max = NiceTop(series.Values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(1).Max() * 1.35);
        foreach (double tick in ChartAxis.Ticks(0, max, 6))
        {
            double x = plot.Left + tick / max * plot.Width; dc.DrawLine(Grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(dc, tick.ToString("0.#"), x, plot.Bottom + 5, 9, center: true);
        }
        double rowHeight = plot.Height / _data.Labels.Length;
        for (int i = 0; i < _data.Labels.Length; i++)
        {
            double y = plot.Top + i * rowHeight, width = (series.Values[i] ?? 0) / max * plot.Width;
            var brush = Ranked[i % Ranked.Length];
            dc.DrawRectangle(brush, null, new Rect(plot.Left, y + rowHeight * .1, Math.Max(0, width), rowHeight * .76));
            Label(dc, _data.Labels[i], 4, y + 2, 10, maxWidth: left - 12);
            string count = series.Counts is { } counts && i < counts.Length ? counts[i].ToString("N0") + " 颗 · " : "";
            Label(dc, count + (series.Values[i] ?? 0).ToString("F1") + "%", plot.Left + width + 7, y + rowHeight * .23, 10, maxWidth: Math.Max(1, plot.Right - plot.Left - width - 8));
        }
        dc.DrawLine(Axis, plot.TopLeft, plot.BottomLeft); dc.DrawLine(Axis, plot.BottomLeft, plot.BottomRight);
        Label(dc, _data.AxisTitle, (plot.Left + plot.Right) / 2, plot.Bottom + 22, 10, center: true);
    }
}
