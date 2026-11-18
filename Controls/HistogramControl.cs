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

    private static readonly Brush InSpecBrush = Freeze(new SolidColorBrush(Color.FromRgb(70, 130, 180)));
    private static readonly Brush OutSpecBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 90, 90)));
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
    private bool _showOverflow = true;
    private long _renderCount;
    public string Unit { get; private set; } = "";
    private string? _lslText, _uslText;
    public string? LslText => _lslText;
    public string? UslText => _uslText;
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
        bool showThreeSigma, string unit = "", string? lslText = null, string? uslText = null, bool showOverflow = true)
    {
        _batch++;
        Unit = unit ?? "";
        _lslText = lslText; _uslText = uslText;
        _showOverflow = showOverflow;
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

    protected override bool HasPlotContent => Bins is { Count: > 0 };

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
        if (Bins is { Count: > 0 } bins && PlotBounds.Contains(position))
        {
            double from = bins[0].From, to = bins[^1].To;
            double value = from + (to - from) * ((position.X - PlotBounds.Left) / PlotBounds.Width);
            int first = 0, last = bins.Count - 1;
            while (first < last)
            {
                int middle = first + (last - first) / 2;
                if (value < bins[middle].To) last = middle;
                else first = middle + 1;
            }
            var bin = bins[first];
            hint = $"总数 {bin.Count}  ·  规格内 {bin.InSpecCount}  ·  规格外 {bin.OutOfSpecCount}";
        }
        if (!Equals(tip.Content, hint)) tip.Content = hint;
    }

    protected override void DrawPlot(DrawingContext dc)
    {
        _renderCount++;
        PlotBounds = Rect.Empty;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (w < 180 || h < 120) return;
        var bins = Bins;
        int maxCount = 1, minPositiveCount = int.MaxValue;
        bool integerBoundaries = true;
        if (bins != null)
        {
            foreach (var bin in bins)
            {
                maxCount = Math.Max(maxCount, bin.Count);
                if (bin.Count > 0) minPositiveCount = Math.Min(minPositiveCount, bin.Count);
                integerBoundaries = integerBoundaries && double.IsInteger(bin.From) && double.IsInteger(bin.To);
            }
        }
        bool useLogFrequency = AutoLogFrequency && maxCount > 100L * minPositiveCount;
        // At narrow widths the heading gets its own row; otherwise all top captions share a row.
        double headerOffset = !string.IsNullOrWhiteSpace(Title) && w < 440 ? ChartAxis.HeadingFontSize(RenderSize) + 7 : 0;
        ChartAxis.DrawTitle(dc, this, useLogFrequency ? "频数（对数）" : "频数（个）", new Rect(6, 3 + headerOffset, 90, 20));
        double legendLeft = DrawLegend(dc, w, headerOffset);
        ChartAxis.DrawHeading(dc, this, Title, headerOffset > 0 ? ChartAxis.PlotLeft : 108,
            headerOffset > 0 ? w - ChartAxis.PlotRight : legendLeft - 10);
        string xTitle = ChartAxis.ValueTitle(Unit);
        var title = ChartAxis.Text(this, xTitle);
        ChartAxis.DrawTitle(dc, this, xTitle, new Rect(Math.Max(4, (ChartAxis.PlotLeft + w - ChartAxis.PlotRight - title.Width) / 2),
            h - ChartAxis.PlotBottom + ChartAxis.AxisTitleGap, Math.Min(w - 8, title.Width + 2), 17));
        if (bins == null || bins.Count == 0) return;

        double dataMin = bins.Min(b => b.From), dataMax = bins.Max(b => b.To);
        double xSpan = dataMax - dataMin;
        if (!double.IsFinite(xSpan) || xSpan <= 0) return;
        const double left = ChartAxis.PlotLeft, right = ChartAxis.PlotRight, bottom = ChartAxis.PlotBottom;
        double plotW = w - left - right;
        if (plotW < 30) return;
        double MapX(double value) => left + (value - dataMin) / xSpan * plotW;
        var references = new List<(double X, FormattedText Text, Pen Pen)>();
        void AddReference(bool show, double? value, string name, Pen pen, string? limitText = null)
        {
            if (!show || !value.HasValue || !double.IsFinite(value.Value) || value < dataMin || value > dataMax) return;
            var text = ChartAxis.Text(this, name + " " + (limitText ?? ChartAxis.Format(value.Value)), 11, pen.Brush);
            text.MaxTextWidth = plotW;
            text.Trimming = TextTrimming.CharacterEllipsis;
            references.Add((MapX(value.Value), text, pen));
        }
        AddReference(ShowLsl, Lsl, "LSL", LslPen, _lslText);
        AddReference(ShowUsl, Usl, "USL", LslPen, _uslText);
        AddReference(ShowMean, Mean, "μ", MeanPen);

        // Keep each label attached to its numeric position; use another lane if labels collide.
        var labels = new List<(double X, FormattedText Text, Pen Pen, Rect Bounds)>();
        foreach (var reference in references.OrderBy(r => r.X))
        {
            double x = Math.Clamp(reference.X - reference.Text.Width / 2, left, left + plotW - reference.Text.Width);
            double labelTop = string.IsNullOrWhiteSpace(Title) ? 22 : Math.Max(22 + headerOffset, ChartAxis.HeadingFontSize(RenderSize) + 10);
            var bounds = new Rect(x, labelTop, reference.Text.Width, reference.Text.Height);
            while (labels.Any(label => Rect.Inflate(label.Bounds, 4, 1).IntersectsWith(bounds))) bounds.Y += 18;
            labels.Add((reference.X, reference.Text, reference.Pen, bounds));
        }
        double top = Math.Max(40 + headerOffset, labels.Count == 0 ? 0 : labels.Max(label => label.Bounds.Bottom) + 5);
        double plotH = h - top - bottom;
        if (plotH < 20) return;
        PlotBounds = new Rect(left, top, plotW, plotH);
        const double countFontSize = 10;
        var widestCount = ShowBinCounts ? ChartAxis.Text(this, maxCount.ToString(CultureInfo.InvariantCulture), countFontSize) : null;
        double binPitch = plotW / bins.Count;
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

        var yTicks = useLogFrequency ? LogFrequencyTicks(maxCount) :
            ChartAxis.Ticks(0, maxCount, Math.Max(1, (int)(plotH / 32)), integers: true);
        double lastFrequencyLabelY = double.PositiveInfinity;
        foreach (double tick in yTicks)
        {
            double y = MapY(tick);
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + plotW, y));
            dc.DrawLine(AxisPen, new Point(left - 4, y), new Point(left, y));
            var text = ChartAxis.Text(this, ChartAxis.Format(tick));
            if (useLogFrequency && lastFrequencyLabelY - y < text.Height + 2) continue;
            dc.DrawText(text, new Point(left - 7 - text.Width, y - text.Height / 2));
            lastFrequencyLabelY = y;
        }
        // Both endpoint captions share the numeric tick row; reserve their space before placing ticks.
        var xTicks = ChartAxis.Ticks(dataMin, dataMax, Math.Max(1, (int)(plotW / 85)), integers: integerBoundaries);
        double xStep = xTicks.Count > 1 ? xTicks[1] - xTicks[0] : xSpan;
        double captionWidth = Math.Max(1, (plotW - 16) / 2);
        var lowerCaption = ChartAxis.Text(this, _showOverflow ? "＜" + ChartAxis.Format(dataMin) : ChartAxis.Format(dataMin, xStep));
        var upperCaption = ChartAxis.Text(this, _showOverflow ? "＞" + ChartAxis.Format(dataMax) : ChartAxis.Format(dataMax, xStep));
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
        foreach (double tick in xTicks)
        {
            double x = MapX(tick);
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
            double x = MapX(bin.From), width = MapX(bin.To) - x;
            double startX = x + Math.Min(0.5, width / 4), barWidth = Math.Max(0.1, width - 1);
            double inSpecTop = MapY(bin.InSpecCount);
            if (bin.InSpecCount > 0)
                dc.DrawRectangle(InSpecBrush, null, new Rect(startX, inSpecTop, barWidth, top + plotH - inSpecTop));
            if (bin.OutOfSpecCount > 0)
            {
                double barTop = MapY(bin.Count);
                dc.DrawRectangle(OutSpecBrush, null, new Rect(startX, barTop, barWidth, inSpecTop - barTop));
            }
        }
        void ReferenceLine(double? value, Pen pen)
        {
            if (value.HasValue && double.IsFinite(value.Value) && value >= dataMin && value <= dataMax)
                dc.DrawLine(pen, new Point(MapX(value.Value), top), new Point(MapX(value.Value), top + plotH));
        }
        if (ShowLsl) ReferenceLine(Lsl, LslPen);
        if (ShowUsl) ReferenceLine(Usl, LslPen);
        if (ShowMean) ReferenceLine(Mean, MeanPen);
        if (ShowThreeSigma && Mean.HasValue && Sigma.HasValue)
        {
            ReferenceLine(Mean - 3 * Sigma, SigmaPen);
            ReferenceLine(Mean + 3 * Sigma, SigmaPen);
        }
        if (widestCount != null)
        {
            var countTexts = new Dictionary<int, FormattedText> { [maxCount] = widestCount };
            for (int index = 0; index < bins.Count; index++)
            {
                var bin = bins[index];
                if (bin.Count <= 0) continue;
                if (!countTexts.TryGetValue(bin.Count, out var text))
                    countTexts[bin.Count] = text = ChartAxis.Text(this, bin.Count.ToString(CultureInfo.InvariantCulture), countFontSize);
                double center = (MapX(bin.From) + MapX(bin.To)) / 2;
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

        foreach (var label in labels)
            dc.DrawLine(label.Pen, new Point(label.X, label.Bounds.Bottom + 1), new Point(label.X, top));
        foreach (var label in labels)
        {
            dc.DrawRectangle(Brushes.White, null, Rect.Inflate(label.Bounds, 2, 1));
            dc.DrawText(label.Text, label.Bounds.TopLeft);
        }
        if (ToolTip is ToolTip { IsOpen: true }) UpdateFrequencyToolTip(Mouse.GetPosition(this));
    }

    private static IEnumerable<double> LogFrequencyTicks(int maxCount)
    {
        yield return 0;
        for (long count = 1; count <= maxCount; count *= 10) yield return count;
    }

    private double DrawLegend(DrawingContext dc, double width, double headerOffset)
    {
        var inSpec = ChartAxis.Text(this, "规格内", 10, Brushes.DimGray);
        var outSpec = ChartAxis.Text(this, "规格外", 10, Brushes.DimGray);
        const double swatch = 10, gap = 4, between = 12;
        double x = width - ChartAxis.PlotRight - (swatch + gap) * 2 - between - inSpec.Width - outSpec.Width;
        double left = x;
        dc.DrawRectangle(InSpecBrush, null, new Rect(x, 5 + headerOffset, swatch, swatch));
        dc.DrawText(inSpec, new Point(x + swatch + gap, 3 + headerOffset));
        x += swatch + gap + inSpec.Width + between;
        dc.DrawRectangle(OutSpecBrush, null, new Rect(x, 5 + headerOffset, swatch, swatch));
        dc.DrawText(outSpec, new Point(x + swatch + gap, 3 + headerOffset));
        return left;
    }
}
