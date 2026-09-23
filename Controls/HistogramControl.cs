using System.Windows;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public class HistogramControl : FrameworkElement
{
    private static readonly PropertyChangedCallback InvalidateIfIdle = (d, _) =>
    {
        var c = (HistogramControl)d;
        if (c._batch == 0)
            c.InvalidateVisual();
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
    private long _renderCount;
    public string Unit { get; private set; } = "";

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

    public void Apply(
        IList<HistogramBin>? bins,
        double? lsl,
        double? usl,
        double? mean,
        double? sigma,
        bool showLsl,
        bool showUsl,
        bool showMean,
        bool showThreeSigma, string unit = "")
    {
        _batch++;
        Unit = unit ?? "";
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
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _renderCount++;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (w < 180 || h < 120) return;
        ChartAxis.DrawTitle(dc, this, "频数（个）", new Rect(6, 3, 90, 20));
        DrawLegend(dc, w);
        string xTitle = ChartAxis.ValueTitle(Unit);
        var title = ChartAxis.Text(this, xTitle);
        ChartAxis.DrawTitle(dc, this, xTitle, new Rect(Math.Max(4, (ChartAxis.PlotLeft + w - ChartAxis.PlotRight - title.Width) / 2),
            h - 22, Math.Min(w - 8, title.Width + 2), 21));
        if (Bins == null || Bins.Count == 0) return;

        double dataMin = Bins.Min(b => b.From), dataMax = Bins.Max(b => b.To);
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax) || dataMax <= dataMin) return;
        int maxCount = Math.Max(1, Bins.Max(b => b.Count));
        const double left = ChartAxis.PlotLeft, right = ChartAxis.PlotRight, bottom = 72;
        double plotW = w - left - right;
        if (plotW < 30) return;
        double MapX(double value) => left + (value - dataMin) / (dataMax - dataMin) * plotW;
        var references = new List<(double X, FormattedText Text, Pen Pen)>();
        void AddReference(bool show, double? value, string name, Pen pen)
        {
            if (!show || !value.HasValue || !double.IsFinite(value.Value) || value < dataMin || value > dataMax) return;
            var text = ChartAxis.Text(this, name + " " + ChartAxis.Format(value.Value), 11, pen.Brush);
            text.MaxTextWidth = plotW;
            text.Trimming = TextTrimming.CharacterEllipsis;
            references.Add((MapX(value.Value), text, pen));
        }
        AddReference(ShowLsl, Lsl, "LSL", LslPen);
        AddReference(ShowUsl, Usl, "USL", LslPen);
        AddReference(ShowMean, Mean, "μ", MeanPen);

        // Keep each label attached to its numeric position; use another lane if labels collide.
        var labels = new List<(double X, FormattedText Text, Pen Pen, Rect Bounds)>();
        foreach (var reference in references.OrderBy(r => r.X))
        {
            double x = Math.Clamp(reference.X - reference.Text.Width / 2, left, left + plotW - reference.Text.Width);
            var bounds = new Rect(x, 22, reference.Text.Width, reference.Text.Height);
            while (labels.Any(label => Rect.Inflate(label.Bounds, 4, 1).IntersectsWith(bounds))) bounds.Y += 18;
            labels.Add((reference.X, reference.Text, reference.Pen, bounds));
        }
        double top = Math.Max(40, labels.Count == 0 ? 0 : labels.Max(label => label.Bounds.Bottom) + 5);
        double plotH = h - top - bottom;
        if (plotH < 20) return;
        double MapY(double value) => top + plotH - value / maxCount * plotH;

        var yTicks = ChartAxis.Ticks(0, maxCount, Math.Max(1, (int)(plotH / 32)), integers: true);
        foreach (double tick in yTicks)
        {
            double y = MapY(tick);
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + plotW, y));
            dc.DrawLine(AxisPen, new Point(left - 4, y), new Point(left, y));
            var text = ChartAxis.Text(this, ChartAxis.Format(tick));
            dc.DrawText(text, new Point(left - 7 - text.Width, y - text.Height / 2));
        }
        var xTicks = ChartAxis.Ticks(dataMin, dataMax, Math.Max(1, (int)(plotW / 85)));
        double xStep = xTicks.Count > 1 ? xTicks[1] - xTicks[0] : dataMax - dataMin;
        double lastLabelRight = double.NegativeInfinity;
        foreach (double tick in xTicks)
        {
            double x = MapX(tick);
            dc.DrawLine(GridPen, new Point(x, top), new Point(x, top + plotH));
            dc.DrawLine(AxisPen, new Point(x, top + plotH), new Point(x, top + plotH + 4));
            var text = ChartAxis.Text(this, ChartAxis.Format(tick, xStep));
            double tx = Math.Clamp(x - text.Width / 2, 2, Math.Max(2, w - text.Width - 2));
            if (tx < lastLabelRight + 8) continue;
            dc.DrawText(text, new Point(tx, top + plotH + 7));
            lastLabelRight = tx + text.Width;
        }

        dc.PushClip(new RectangleGeometry(new Rect(left, top, plotW, plotH)));
        foreach (var bin in Bins)
        {
            double x = MapX(bin.From), width = MapX(bin.To) - x;
            double barH = (double)Math.Max(0, bin.Count) / maxCount * plotH;
            dc.DrawRectangle(bin.IsOutOfSpec ? OutSpecBrush : InSpecBrush, null,
                new Rect(x + Math.Min(0.5, width / 4), top + plotH - barH, Math.Max(0.1, width - 1), barH));
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
        dc.Pop();
        ChartAxis.DrawAxes(dc, AxisPen, new Rect(left, top, plotW, plotH));

        // Separate boundary captions from the numeric ticks and the axis title.
        double captionWidth = (plotW - 8) / 2;
        var lowerCaption = ChartAxis.Text(this, "＜" + ChartAxis.Format(dataMin));
        var upperCaption = ChartAxis.Text(this, "＞" + ChartAxis.Format(dataMax));
        foreach (var caption in new[] { lowerCaption, upperCaption })
        {
            caption.MaxTextWidth = captionWidth;
            caption.MaxTextHeight = 20;
            caption.Trimming = TextTrimming.CharacterEllipsis;
        }
        dc.DrawText(lowerCaption, new Point(left, top + plotH + 27));
        dc.DrawText(upperCaption, new Point(left + plotW - upperCaption.Width, top + plotH + 27));

        foreach (var label in labels)
            dc.DrawLine(label.Pen, new Point(label.X, label.Bounds.Bottom + 1), new Point(label.X, top));
        foreach (var label in labels)
        {
            dc.DrawRectangle(Brushes.White, null, Rect.Inflate(label.Bounds, 2, 1));
            dc.DrawText(label.Text, label.Bounds.TopLeft);
        }
    }

    private void DrawLegend(DrawingContext dc, double width)
    {
        var inSpec = ChartAxis.Text(this, "规格内", 10, Brushes.DimGray);
        var outSpec = ChartAxis.Text(this, "规格外", 10, Brushes.DimGray);
        const double swatch = 10, gap = 4, between = 12;
        double x = width - ChartAxis.PlotRight - (swatch + gap) * 2 - between - inSpec.Width - outSpec.Width;
        dc.DrawRectangle(InSpecBrush, null, new Rect(x, 5, swatch, swatch));
        dc.DrawText(inSpec, new Point(x + swatch + gap, 3));
        x += swatch + gap + inSpec.Width + between;
        dc.DrawRectangle(OutSpecBrush, null, new Rect(x, 5, swatch, swatch));
        dc.DrawText(outSpec, new Point(x + swatch + gap, 3));
    }
}
