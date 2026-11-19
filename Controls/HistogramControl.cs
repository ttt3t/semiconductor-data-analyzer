using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public class HistogramControl : CachedPlotControl
{
    // Bars/ticks are inexpensive vectors; keep them sharp while resizing, without a second bitmap.
    public HistogramControl() => UseBitmapCache = false;

    private static readonly PropertyChangedCallback InvalidateIfIdle = (d, _) =>
    {
        var c = (HistogramControl)d;
        if (c._batch == 0)
            c.InvalidatePlot();
    };

    public static readonly DependencyProperty BinsProperty =
        DependencyProperty.Register(nameof(Bins), typeof(IList<HistogramBin>), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, InvalidateIfIdle));

    public static readonly DependencyProperty LslProperty =
        DependencyProperty.Register(nameof(Lsl), typeof(double?), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, InvalidateIfIdle));

    public static readonly DependencyProperty UslProperty =
        DependencyProperty.Register(nameof(Usl), typeof(double?), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, InvalidateIfIdle));

    public static readonly DependencyProperty MeanProperty =
        DependencyProperty.Register(nameof(Mean), typeof(double?), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, InvalidateIfIdle));

    public static readonly DependencyProperty SigmaProperty =
        DependencyProperty.Register(nameof(Sigma), typeof(double?), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, InvalidateIfIdle));

    public static readonly DependencyProperty ShowLslProperty =
        DependencyProperty.Register(nameof(ShowLsl), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(true, InvalidateIfIdle));

    public static readonly DependencyProperty ShowUslProperty =
        DependencyProperty.Register(nameof(ShowUsl), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(true, InvalidateIfIdle));

    public static readonly DependencyProperty ShowMeanProperty =
        DependencyProperty.Register(nameof(ShowMean), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(true, InvalidateIfIdle));

    public static readonly DependencyProperty ShowThreeSigmaProperty =
        DependencyProperty.Register(nameof(ShowThreeSigma), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(true, InvalidateIfIdle));

    public static readonly DependencyProperty ShowBinCountsProperty =
        DependencyProperty.Register(nameof(ShowBinCounts), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(false, InvalidateIfIdle));

    public static readonly DependencyProperty AutoLogFrequencyProperty =
        DependencyProperty.Register(nameof(AutoLogFrequency), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(true, InvalidateIfIdle));

    public static readonly DependencyProperty ShowPercentageProperty =
        DependencyProperty.Register(nameof(ShowPercentage), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(false, InvalidateIfIdle));

    public static readonly DependencyProperty ShowCumulativePercentageProperty =
        DependencyProperty.Register(nameof(ShowCumulativePercentage), typeof(bool), typeof(HistogramControl),
            new FrameworkPropertyMetadata(false, InvalidateIfIdle));

    private static readonly Brush InSpecBrush = Freeze(new SolidColorBrush(Color.FromRgb(70, 130, 180)));
    private static readonly Brush OutSpecBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 90, 90)));
    private static readonly Pen InSpecPointPen = Freeze(new Pen(InSpecBrush, 3));
    private static readonly Pen OutOfSpecPointPen = Freeze(new Pen(OutSpecBrush, 3));
    private static readonly Pen CumulativePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(124, 58, 175))), 1.8));
    private static readonly Pen AxisPen = Freeze(new Pen(Brushes.Black, 1.2));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(230, 230, 230))), 1));
    private static readonly Pen LslPen = Freeze(new Pen(Brushes.Red, 2.0));
    private static readonly Pen MeanPen = Freeze(new Pen(Brushes.DarkGreen, 2.0));
    private static readonly Pen SigmaPen = Freeze(new Pen(Brushes.Orange, 1.5) { DashStyle = DashStyles.Dash });

    private static T Freeze<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    private int _batch;
    private long _renderCount;
    private HistogramHorizontalScale? _horizontalScale;
    private long _visibleCount;
    public string Unit { get; private set; } = "";
    private string? _lslText, _uslText;
    public string? LslText => _lslText;
    public string? UslText => _uslText;
    internal HistogramView? View { get; private set; }
    internal Rect PlotBounds { get; private set; } = Rect.Empty;

    public IList<HistogramBin>? Bins
    {
        get => (IList<HistogramBin>?)GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    public double? Lsl { get => (double?)GetValue(LslProperty); set => SetValue(LslProperty, value); }
    public double? Usl { get => (double?)GetValue(UslProperty); set => SetValue(UslProperty, value); }
    public double? Mean { get => (double?)GetValue(MeanProperty); set => SetValue(MeanProperty, value); }
    public double? Sigma { get => (double?)GetValue(SigmaProperty); set => SetValue(SigmaProperty, value); }
    public bool ShowLsl { get => (bool)GetValue(ShowLslProperty); set => SetValue(ShowLslProperty, value); }
    public bool ShowUsl { get => (bool)GetValue(ShowUslProperty); set => SetValue(ShowUslProperty, value); }
    public bool ShowMean { get => (bool)GetValue(ShowMeanProperty); set => SetValue(ShowMeanProperty, value); }
    public bool ShowThreeSigma { get => (bool)GetValue(ShowThreeSigmaProperty); set => SetValue(ShowThreeSigmaProperty, value); }
    public bool ShowPercentage { get => (bool)GetValue(ShowPercentageProperty); set => SetValue(ShowPercentageProperty, value); }
    public bool ShowCumulativePercentage { get => (bool)GetValue(ShowCumulativePercentageProperty); set => SetValue(ShowCumulativePercentageProperty, value); }
    public bool ShowBinCounts { get => (bool)GetValue(ShowBinCountsProperty); set => SetValue(ShowBinCountsProperty, value); }
    public bool AutoLogFrequency { get => (bool)GetValue(AutoLogFrequencyProperty); set => SetValue(AutoLogFrequencyProperty, value); }

    public void Apply(
        IList<HistogramBin>? bins,
        double? lsl,
        double? usl,
        double? mean,
        double? sigma,
        bool showLsl,
        bool showUsl,
        bool showMean,
        bool showThreeSigma, string unit = "", string? lslText = null, string? uslText = null, HistogramView? view = null)
    {
        _batch++;
        Unit = unit ?? "";
        _lslText = lslText; _uslText = uslText;
        View = view;
        Bins = bins;
        Lsl = lsl;
        Usl = usl;
        Mean = mean;
        Sigma = sigma;
        ShowLsl = showLsl;
        ShowUsl = showUsl;
        ShowMean = showMean;
        ShowThreeSigma = showThreeSigma;
        _batch--;
        InvalidatePlot();
    }

    protected override bool HasPlotContent => View.HasValue || Bins is { Count: > 0 };

    protected override void OnToolTipOpening(ToolTipEventArgs e)
    {
        UpdateFrequencyToolTip(Mouse.GetPosition(this));
        base.OnToolTipOpening(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (ToolTip is ToolTip { IsOpen: true } && !IsMouseCaptured)
            UpdateFrequencyToolTip(e.GetPosition(this));
        base.OnMouseMove(e);
    }

    private void UpdateFrequencyToolTip(Point position)
    {
        if (ToolTip is not ToolTip tip) return;
        string hint = "按住左键左右拖动横轴范围，滚轮缩放，双击恢复默认范围";
        if (Bins is { Count: > 0 } bins && _horizontalScale is { } scale && PlotBounds.Contains(position))
        {
            HistogramBin? bin;
            if (scale.IntegerBins) bin = scale.HitIntegerBin(bins, position.X);
            else
            {
                bin = bins.Where(b => b.Count > 0 && b.From == b.To && Math.Abs(position.X - scale.Map(b.From)) <= 3)
                    .MinBy(b => Math.Abs(position.X - scale.Map(b.From)));
                if (bin == null)
                {
                    int index = HistogramService.FindBin(bins, scale.Unmap(position.X));
                    if (index >= 0) bin = bins[index];
                }
            }
            if (bin != null)
            {
                string start = bin.IncludesFrom ? "[" : "(";
                string end = bin.IncludesTo ? "]" : ")";
                string interval = start + bin.From.ToString("G", CultureInfo.InvariantCulture) + ", " +
                    bin.To.ToString("G", CultureInfo.InvariantCulture) + end;
                hint = $"{interval}  ·  数量 {bin.Count}  ·  {FormatPercentage(Percentage(bin.Count, _visibleCount))}  ·  {(bin.IsOutOfSpec ? "规格外" : "规格内")}";
            }
        }
        if (!Equals(tip.Content, hint)) tip.Content = hint;
    }

    protected override void DrawPlot(DrawingContext dc)
    {
        _renderCount++;
        PlotBounds = Rect.Empty;
        _horizontalScale = null;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (w < 160 || h < 90) return;
        var bins = Bins ?? Array.Empty<HistogramBin>();
        int maxCount = 1, minPositiveCount = int.MaxValue;
        bool integerBoundaries = bins.Count > 0;
        _visibleCount = 0;
        foreach (var bin in bins)
        {
            _visibleCount += bin.Count;
            maxCount = Math.Max(maxCount, bin.Count);
            if (bin.Count > 0) minPositiveCount = Math.Min(minPositiveCount, bin.Count);
            integerBoundaries = integerBoundaries && double.IsInteger(bin.From) && double.IsInteger(bin.To);
        }
        bool useLogFrequency = AutoLogFrequency && maxCount > 100L * minPositiveCount;
        long visibleCount = _visibleCount;
        bool showCumulative = ShowCumulativePercentage && visibleCount > 0;
        double left = ShowPercentage ? 74 : ChartAxis.PlotLeft;
        double right = showCumulative ? 48 : ChartAxis.PlotRight;
        const double bottom = ChartAxis.PlotBottom;
        double plotW = w - left - right;
        if (plotW < 30) return;

        // At narrow widths the heading gets its own row; otherwise all top captions share a row.
        double headerOffset = !string.IsNullOrWhiteSpace(Title) && w < 440 ? ChartAxis.HeadingFontSize(RenderSize) + 7 : 0;
        string frequencyTitle = ShowPercentage ? "频数（%）" : "频数（个）";
        if (useLogFrequency) frequencyTitle = ShowPercentage ? "百分比（对数）" : "频数（对数）";
        ChartAxis.DrawTitle(dc, this, frequencyTitle, new Rect(6, 3 + headerOffset, 100, 20));
        double legendLeft = w - right >= 198 ? DrawLegend(dc, w - right, headerOffset) : w - right;
        if (showCumulative)
            dc.DrawText(ChartAxis.Text(this, "累计 %", 11, CumulativePen.Brush), new Point(w - right + 5, 3 + headerOffset));
        ChartAxis.DrawHeading(dc, this, Title, headerOffset > 0 ? ChartAxis.PlotLeft : 108,
            headerOffset > 0 ? w - ChartAxis.PlotRight : legendLeft - 10);
        if (!HasPlotContent) return;

        double dataMin = View?.From ?? bins.Min(b => b.From), dataMax = View?.To ?? bins.Max(b => b.To);
        double xSpan = dataMax - dataMin;
        if (!double.IsFinite(xSpan) || xSpan <= 0) return;
        var integerLimits = View?.IntegerLimits;
        var scale = new HistogramHorizontalScale(dataMin, dataMax, left, plotW, bins, integerLimits.HasValue);
        double MapX(double value) => scale.Map(value);

        string xTitle = ChartAxis.ValueTitle(Unit);
        if (View is { } view && view.RequestedBins != bins.Count)
            xTitle += $" · {bins.Count} 箱（设定 {view.RequestedBins}）";
        if (scale.IsAdjusted) xTitle += " · 局部横轴拉伸";
        var title = ChartAxis.Text(this, xTitle);
        ChartAxis.DrawTitle(dc, this, xTitle, new Rect(Math.Max(4, left + (plotW - title.Width) / 2),
            h - bottom + ChartAxis.AxisTitleGap, Math.Min(w - 8, title.Width + 2), 17));

        var references = new List<(double X, FormattedText Text, Pen Pen)>();
        void AddReference(double? position, double? value, string name, Pen pen, string? limitText = null)
        {
            if (position is not double x || value is not double measurement) return;
            var text = ChartAxis.Text(this, name + " " + (limitText ?? ChartAxis.Format(measurement)), 11, pen.Brush);
            text.MaxTextWidth = plotW;
            text.Trimming = TextTrimming.CharacterEllipsis;
            references.Add((x, text, pen));
        }
        double? lowerPosition = ShowLsl ? scale.ReferencePosition(Lsl, integerLimits?.FirstAllowed) : null;
        double? upperPosition = ShowUsl ? scale.ReferencePosition(Usl, integerLimits?.FirstAbove) : null;
        double? meanPosition = ShowMean ? scale.ReferencePosition(Mean) : null;
        AddReference(lowerPosition, Lsl, "LSL", LslPen, _lslText);
        AddReference(upperPosition, Usl, "USL", LslPen, _uslText);
        AddReference(meanPosition, Mean, "μ", MeanPen);

        // Add lanes when reference labels collide, keeping each attached to its line.
        var labels = new List<(double X, FormattedText Text, Pen Pen, Rect Bounds)>();
        foreach (var reference in references.OrderBy(r => r.X))
        {
            double x = Math.Clamp(reference.X - reference.Text.Width / 2, left, left + plotW - reference.Text.Width);
            double labelTop = string.IsNullOrWhiteSpace(Title) ? 22 : Math.Max(22 + headerOffset, ChartAxis.HeadingFontSize(RenderSize) + 10);
            var bounds = new Rect(x, labelTop, reference.Text.Width, reference.Text.Height);
            while (labels.Any(label => Rect.Inflate(label.Bounds, 4, 1).IntersectsWith(bounds))) bounds.Y += 18;
            if (bounds.Bottom <= h - bottom - 25) labels.Add((reference.X, reference.Text, reference.Pen, bounds));
        }
        double top = Math.Max((h >= 120 ? 40 : 27) + headerOffset,
            labels.Count == 0 ? 0 : labels.Max(label => label.Bounds.Bottom) + 5);
        double plotH = h - top - bottom;
        if (plotH < 20) return;
        PlotBounds = new Rect(left, top, plotW, plotH);
        _horizontalScale = scale;
        const double countFontSize = 10;
        string BarLabel(int count) => ShowPercentage ? FormatPercentage(Percentage(count, visibleCount))
            : count.ToString(CultureInfo.InvariantCulture);
        var widestCount = ShowBinCounts && visibleCount > 0
            ? ChartAxis.Text(this, ShowPercentage ? "99.99%" : BarLabel(maxCount), countFontSize) : null;
        double binPitch = plotW / Math.Max(1, bins.Count);
        bool rotateCounts = widestCount != null && binPitch < widestCount.Width + 4;
        double countLabelHeight = widestCount == null ? 0 : rotateCounts ? widestCount.Width : widestCount.Height;
        int countLanes = rotateCounts ? (int)Math.Clamp(Math.Ceiling(widestCount!.Height / binPitch), 1, 4) : 1;
        double laneStep = countLanes > 1 ? Math.Min(countLabelHeight + 2,
            Math.Max(0, plotH / 2 - countLabelHeight - 3) / (countLanes - 1)) : 0;
        double countHeadroom = widestCount == null ? 0 : Math.Min(plotH / 2, countLabelHeight + 3 + (countLanes - 1) * laneStep);
        // log10(1 + count) keeps zero at the baseline and labels in sample counts.
        double FrequencyPosition(double count) => useLogFrequency ? Math.Log10(1 + count) : count;
        double maxFrequencyPosition = FrequencyPosition(maxCount);
        double MapY(double count) => top + plotH - FrequencyPosition(count) / maxFrequencyPosition * (plotH - countHeadroom);

        int yTarget = Math.Max(1, (int)(plotH / 16));
        bool percentTicks = ShowPercentage && !useLogFrequency && visibleCount > 0;
        var yTicks = useLogFrequency ? LogFrequencyTicks(maxCount)
            : percentTicks ? ChartAxis.Ticks(0, Percentage(maxCount, visibleCount), yTarget, maxTarget: 40)
            : ChartAxis.Ticks(0, maxCount, yTarget, integers: true, maxTarget: 40);
        double lastFrequencyLabelTop = double.PositiveInfinity;
        foreach (double tick in yTicks)
        {
            double count = percentTicks ? tick * visibleCount / 100 : tick;
            double y = MapY(count);
            double value = ShowPercentage && !percentTicks ? Percentage(tick, visibleCount) : tick;
            if (ShowPercentage && visibleCount == 0 && tick > 0) continue;
            string label = ShowPercentage ? ChartAxis.Format(value) + "%" : ChartAxis.Format(tick);
            var labelText = ChartAxis.Text(this, label);
            double labelTop = y - labelText.Height / 2;
            if (labelTop + labelText.Height + 2 > lastFrequencyLabelTop) continue;
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + plotW, y));
            dc.DrawLine(AxisPen, new Point(left - 4, y), new Point(left, y));
            dc.DrawText(labelText, new Point(Math.Max(2, left - 7 - labelText.Width), labelTop));
            lastFrequencyLabelTop = labelTop;
        }
        // Both endpoint captions share the numeric tick row; reserve their space before placing ticks.
        var xTicks = ChartAxis.Ticks(dataMin, dataMax, Math.Max(1, (int)(plotW / 42.5)), integers: integerBoundaries, maxTarget: 40);
        double xStep = xTicks.Count > 1 ? xTicks[1] - xTicks[0] : xSpan;
        double captionWidth = Math.Max(1, (plotW - 16) / 2);
        string lower = ChartAxis.Format(dataMin, xStep), upper = ChartAxis.Format(dataMax, xStep);
        var lowerCaption = ChartAxis.Text(this, View is { Below: > 0 } leftView ? $"＜{lower} · {leftView.Below} 个" : lower);
        var upperCaption = ChartAxis.Text(this, View is { Above: > 0 } rightView ? $"＞{upper} · {rightView.Above} 个" : upper);
        foreach (var caption in new[] { lowerCaption, upperCaption })
        {
            caption.MaxTextWidth = captionWidth;
            caption.MaxLineCount = 1;
            caption.Trimming = TextTrimming.CharacterEllipsis;
        }
        double lowerWidth = Math.Min(captionWidth, lowerCaption.WidthIncludingTrailingWhitespace);
        double upperWidth = Math.Min(captionWidth, upperCaption.WidthIncludingTrailingWhitespace);
        double lowerX = Math.Max(2, left - lowerWidth / 2);
        double upperX = Math.Min(w - upperWidth - 2, left + plotW - upperWidth / 2);
        double tickY = top + plotH + ChartAxis.TickLabelGap;
        dc.DrawText(lowerCaption, new Point(lowerX, tickY));
        dc.DrawText(upperCaption, new Point(upperX, tickY));
        double lastLabelRight = lowerX + lowerWidth;
        double lastGridX = left;
        foreach (double tick in xTicks)
        {
            if (tick <= dataMin || tick >= dataMax) continue;
            double x = MapX(tick);
            if (x - lastGridX < 5 || PlotBounds.Right - x < 5) continue;
            lastGridX = x;
            dc.DrawLine(GridPen, new Point(x, top), new Point(x, top + plotH));
            dc.DrawLine(AxisPen, new Point(x, top + plotH), new Point(x, top + plotH + 4));
            var text = ChartAxis.Text(this, ChartAxis.Format(tick, xStep));
            double tx = Math.Clamp(x - text.Width / 2, 2, Math.Max(2, w - text.Width - 2));
            if (tx < lastLabelRight + 8 || tx + text.Width > upperX - 8) continue;
            dc.DrawText(text, new Point(tx, tickY));
            lastLabelRight = tx + text.Width;
        }

        dc.PushClip(new RectangleGeometry(PlotBounds));
        foreach (var bin in bins)
        {
            if (bin.Count == 0) continue;
            var (x, endX) = scale.MapBin(bin);
            double width = endX - x;
            if (width <= 0) continue;
            double gap = Math.Min(0.5, width / 4);
            if (!bin.IsOutOfSpec) gap = Math.Min(gap, Math.Max(0, (width - scale.MinimumBarWidth) / 2));
            double barTop = MapY(bin.Count);
            dc.DrawRectangle(bin.IsOutOfSpec ? OutSpecBrush : InSpecBrush, null,
                new Rect(x + gap, barTop, width - 2 * gap, top + plotH - barTop));
        }
        void ReferenceLine(double? position, Pen pen)
        {
            if (position is double x)
                dc.DrawLine(pen, new Point(x, top), new Point(x, top + plotH));
        }
        ReferenceLine(lowerPosition, LslPen);
        ReferenceLine(upperPosition, LslPen);
        ReferenceLine(meanPosition, MeanPen);
        if (ShowThreeSigma && Mean.HasValue && Sigma.HasValue)
        {
            ReferenceLine(scale.ReferencePosition(Mean - 3 * Sigma), SigmaPen);
            ReferenceLine(scale.ReferencePosition(Mean + 3 * Sigma), SigmaPen);
        }
        foreach (var bin in bins)
        {
            if (bin.Count == 0) continue;
            var (x, endX) = scale.MapBin(bin);
            if (x != endX) continue;
            dc.DrawLine(bin.IsOutOfSpec ? OutOfSpecPointPen : InSpecPointPen,
                new Point(x, MapY(bin.Count)), new Point(x, top + plotH));
        }
        if (showCumulative) DrawCumulativeLine(dc, bins, scale, PlotBounds, visibleCount);
        if (widestCount != null)
        {
            var countTexts = new Dictionary<int, FormattedText>();
            for (int index = 0; index < bins.Count; index++)
            {
                var bin = bins[index];
                if (bin.Count <= 0) continue;
                if (!countTexts.TryGetValue(bin.Count, out var text))
                    countTexts[bin.Count] = text = ChartAxis.Text(this, BarLabel(bin.Count), countFontSize);
                var (binLeft, binRight) = scale.MapBin(bin);
                double center = (binLeft + binRight) / 2;
                double width = rotateCounts ? text.Height : text.Width;
                double x = Math.Clamp(center - width / 2, left, left + plotW - width);
                double y = MapY(bin.Count) - 3 - (index % countLanes) * laneStep;
                if (rotateCounts)
                {
                    y = Math.Max(top + text.Width, y);
                    dc.PushTransform(new RotateTransform(-90, x, y));
                    dc.DrawText(text, new Point(x, y));
                    dc.Pop();
                }
                else dc.DrawText(text, new Point(x, Math.Max(top, y - text.Height)));
            }
        }
        dc.Pop();
        ChartAxis.DrawAxes(dc, AxisPen, PlotBounds);
        if (showCumulative) DrawCumulativeAxis(dc, PlotBounds, yTarget);

        foreach (var label in labels)
            dc.DrawLine(label.Pen, new Point(label.X, label.Bounds.Bottom + 1), new Point(label.X, top));
        foreach (var label in labels)
        {
            dc.DrawRectangle(Brushes.White, null, Rect.Inflate(label.Bounds, 2, 1));
            dc.DrawText(label.Text, label.Bounds.TopLeft);
        }
        if (ToolTip is ToolTip { IsOpen: true }) UpdateFrequencyToolTip(Mouse.GetPosition(this));
    }

    private static double Percentage(double count, long total) => total > 0 ? count / total * 100 : 0;

    private static string FormatPercentage(double value) => value is > 0 and < .01
        ? "<0.01%" : value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static void DrawCumulativeLine(DrawingContext dc, IList<HistogramBin> bins,
        HistogramHorizontalScale scale, Rect plot, long total)
    {
        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            long cumulative = 0;
            double y = plot.Bottom;
            path.BeginFigure(plot.BottomLeft, false, false);
            foreach (var bin in bins)
            {
                if (bin.Count <= 0) continue;
                path.LineTo(new Point(scale.Map(bin.From), y), true, false);
                cumulative += bin.Count;
                y = plot.Bottom - (double)cumulative / total * plot.Height;
                path.LineTo(new Point(scale.Map(bin.To), y), true, false);
            }
            path.LineTo(new Point(plot.Right, y), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, CumulativePen, line);
    }

    private void DrawCumulativeAxis(DrawingContext dc, Rect plot, int target)
    {
        dc.DrawLine(CumulativePen, plot.TopRight, plot.BottomRight);
        double lastLabelTop = double.PositiveInfinity;
        foreach (double value in ChartAxis.Ticks(0, 100, target, maxTarget: 40))
        {
            double y = plot.Bottom - value / 100 * plot.Height;
            var text = ChartAxis.Text(this, ChartAxis.Format(value) + "%", 11, CumulativePen.Brush);
            double top = y - text.Height / 2;
            if (top + text.Height + 2 > lastLabelTop ||
                (value < 100 && value > 0 && top < plot.Top + text.Height / 2 + 2)) continue;
            dc.DrawLine(CumulativePen, new Point(plot.Right, y), new Point(plot.Right + 4, y));
            dc.DrawText(text, new Point(plot.Right + 7, top));
            lastLabelTop = top;
        }
    }

    private static IEnumerable<double> LogFrequencyTicks(int maxCount)
    {
        yield return 0;
        for (long count = 1; count <= maxCount; count *= 10)
        {
            yield return count;
            if (3 * count <= maxCount) yield return 3 * count;
        }
    }

    private double DrawLegend(DrawingContext dc, double right, double headerOffset)
    {
        var inSpec = ChartAxis.Text(this, "规格内", 10, Brushes.DimGray);
        var outSpec = ChartAxis.Text(this, "规格外", 10, Brushes.DimGray);
        const double swatch = 10, gap = 4, between = 12;
        double x = right - (swatch + gap) * 2 - between - inSpec.Width - outSpec.Width;
        double left = x;
        dc.DrawRectangle(InSpecBrush, null, new Rect(x, 5 + headerOffset, swatch, swatch));
        dc.DrawText(inSpec, new Point(x + swatch + gap, 3 + headerOffset));
        x += swatch + gap + inSpec.Width + between;
        dc.DrawRectangle(OutSpecBrush, null, new Rect(x, 5 + headerOffset, swatch, swatch));
        dc.DrawText(outSpec, new Point(x + swatch + gap, 3 + headerOffset));
        return left;
    }
}
