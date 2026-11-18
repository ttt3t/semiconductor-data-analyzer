using System.Buffers;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class HistogramBin
{
    public double From { get; set; }
    public double To { get; set; }
    public int Count { get; set; }
    public int OutOfSpecCount { get; set; }
    public int InSpecCount => Count - OutOfSpecCount;
}

public static class HistogramService
{
    private readonly record struct BinSection(double From, double To, int Offset, int Count)
    {
        public double Width { get; } = (To - From) / Count;

        public int Index(double value)
        {
            if (value <= From) return Offset;
            if (value >= To) return Offset + Count - 1;
            return Offset + Math.Min(Count - 1, (int)((value - From) / Width));
        }
    }

    public static List<HistogramBin> CreateFixedBins(
        IReadOnlyList<TestValue> values, int binCount, double? lsl, double? usl, bool includeOutOfLimit,
        CancellationToken token = default, double? integerBinFactor = null)
    {
        if (values.Count == 0 || binCount < 1) return new();
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var read = ValueReader(values);
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value)) continue;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (!includeOutOfLimit)
        {
            double allowedMin = lsl.HasValue ? Math.Max(min, lsl.Value) : min;
            double allowedMax = usl.HasValue ? Math.Min(max, usl.Value) : max;
            if (allowedMax >= allowedMin) (min, max) = (allowedMin, allowedMax);
        }
        (min, max) = PadConstant(min, max);
        return CreateRangeBins(values, binCount, min, max, lsl, usl, includeOutOfLimit, false, true, token, integerBinFactor);
    }

    public static List<HistogramBin> CreateSpecZoneBins(
        IReadOnlyList<TestValue> values, double lsl, double usl, bool includeOutOfLimit,
        int binCount = AnalyzerConfig.DefaultSpecZoneBinCount, CancellationToken token = default, double? integerBinFactor = null)
    {
        if (values.Count == 0 || usl <= lsl) return new();
        // Preserve the original spec range: each tail covers 40/70 of the distance between the limits.
        double tail = (usl - lsl) * (4.0 / 7.0);
        return CreateRangeBins(values, binCount, lsl - tail, usl + tail, lsl, usl, includeOutOfLimit, true, true, token, integerBinFactor);
    }

    public static List<HistogramBin> CreateMeanCenteredBins(
        IReadOnlyList<TestValue> values, double? lsl, double? usl, bool includeOutOfLimit,
        out double center, out double scale, int binCount = AnalyzerConfig.DefaultMedianBinCount,
        CancellationToken token = default, double? integerBinFactor = null)
    {
        center = scale = 0;
        if (values.Count == 0 || binCount < 1) return new();

        double[] sorted = ArrayPool<double>.Shared.Rent(values.Count);
        try
        {
            var read = ValueReader(values);
            int count = 0;
            for (int index = 0; index < values.Count; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                double value = read(index);
                if (double.IsFinite(value)) sorted[count++] = value;
            }
            if (count == 0) return new();
            Array.Sort(sorted, 0, count);
            token.ThrowIfCancellationRequested();
            center = Percentile(sorted, count, 0.5);
            scale = (Percentile(sorted, count, 0.75) - Percentile(sorted, count, 0.25)) / 1.349;
        }
        finally { ArrayPool<double>.Shared.Return(sorted); }

        // The default view spans ±8 robust standard deviations around the median.
        var (from, to) = PadConstant(center - 8 * scale, center + 8 * scale);
        return CreateRangeBins(values, binCount, from, to, lsl, usl, includeOutOfLimit, false, true, token, integerBinFactor);
    }

    internal static List<HistogramBin> CreateRangeBins(
        IReadOnlyList<TestValue> values, int binCount, double from, double to, double? lsl, double? usl,
        bool includeOutOfLimit, bool splitAtLimits, bool includeOverflow, CancellationToken token, double? integerBinFactor = null)
    {
        if (values.Count == 0 || binCount < 1) return new();
        bool integerBins = integerBinFactor.HasValue;
        double factor = integerBinFactor ?? 1;
        if (integerBins)
            (from, to) = IntegerRange(from * factor, to * factor, binCount, includeOverflow);
        double? plotLsl = lsl * factor, plotUsl = usl * factor;
        double span = to - from;
        if (!double.IsFinite(span) || !(span / binCount > 0)) return new();

        Span<double> edges = stackalloc double[4];
        int edgeCount = 0;
        edges[edgeCount++] = from;
        if (splitAtLimits && plotLsl is double low && plotUsl is double high && high > low)
        {
            if (low > from && low < to && (!integerBins || double.IsInteger(low))) edges[edgeCount++] = low;
            if (high > from && high < to && (!integerBins || double.IsInteger(high))) edges[edgeCount++] = high;
        }
        if (edgeCount > binCount) edgeCount = 1;
        edges[edgeCount++] = to;

        Span<BinSection> sections = stackalloc BinSection[3];
        var bins = new List<HistogramBin>(binCount);
        for (int index = 1; index < edgeCount; index++)
        {
            int end = binCount;
            if (index < edgeCount - 1)
            {
                int min = bins.Count + 1, max = binCount - (edgeCount - 1 - index);
                if (integerBins)
                {
                    min = Math.Max(min, binCount - (int)Math.Min(binCount, to - edges[index]));
                    max = Math.Min(max, bins.Count + (int)Math.Min(binCount - bins.Count, edges[index] - edges[index - 1]));
                }
                end = Math.Clamp((int)Math.Round((edges[index] - from) / span * binCount, MidpointRounding.AwayFromZero), min, max);
            }
            BinSection section = new(edges[index - 1], edges[index], bins.Count, end - bins.Count);
            if (!(section.Width > 0)) return new();
            sections[index - 1] = section;
            double lower = section.From;
            for (int bin = 0; bin < section.Count; bin++)
            {
                if ((bin & 4095) == 0) token.ThrowIfCancellationRequested();
                double upper = bin == section.Count - 1 ? section.To :
                    Math.Min(section.To, section.From + (bin + 1) * section.Width);
                if (integerBins) upper = Math.Round(upper);
                bins.Add(new HistogramBin { From = lower, To = upper });
                lower = upper;
            }
        }

        var read = ValueReader(values);
        int sectionCount = edgeCount - 1;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value)) continue;
            bool outOfSpec = value < lsl || value > usl;
            double plottedValue = value * factor;
            if ((!includeOutOfLimit && outOfSpec) ||
                (!includeOverflow && (plottedValue < from || plottedValue > to))) continue;
            int section = 0;
            while (section + 1 < sectionCount &&
                (plottedValue > sections[section].To || plottedValue == sections[section].To && plottedValue != plotUsl))
                section++;
            var segment = sections[section];
            int binIndex = segment.Index(plottedValue);
            if (integerBins)
            {
                // Rounded boundaries in a specification section can differ from the uniform estimate.
                if (binIndex > segment.Offset && plottedValue < bins[binIndex].From) binIndex--;
                else if (binIndex + 1 < segment.Offset + segment.Count && plottedValue >= bins[binIndex].To) binIndex++;
            }
            var bin = bins[binIndex];
            bin.Count++;
            if (outOfSpec) bin.OutOfSpecCount++;
        }
        if (factor != 1)
            foreach (var bin in bins) { bin.From /= factor; bin.To /= factor; }
        return bins;
    }

    internal static bool HasIntegerValues(IReadOnlyList<TestValue> values, double displayFactor, CancellationToken token)
    {
        var read = ValueReader(values);
        bool any = false;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value)) continue;
            if (!double.IsInteger(value * displayFactor)) return false;
            any = true;
        }
        return any;
    }

    private static (double From, double To) IntegerRange(double from, double to, int binCount, bool coverOriginal)
    {
        double span = to - from;
        if (!double.IsFinite(span) || span < 0) return (from, to);
        double center = from + span / 2;
        double magnitude = Math.Max(Math.Abs(from), Math.Abs(to));
        double minWidth = Math.Max(1, Math.BitIncrement(magnitude) - magnitude);
        if (!double.IsFinite(minWidth)) return (from, to);
        double width = Math.Max(minWidth, coverOriginal
            ? Math.Ceiling((Math.Ceiling(to) - Math.Floor(from)) / binCount)
            : Math.Round(span / binCount, MidpointRounding.AwayFromZero));
        double integerSpan = width * binCount;
        double start = Math.Round(center - integerSpan / 2);
        if (coverOriginal) start = Math.Clamp(start, Math.Ceiling(to) - integerSpan, Math.Floor(from));
        return (start, start + integerSpan);
    }

    private static (double From, double To) PadConstant(double from, double to)
    {
        if (from != to || !double.IsFinite(from)) return (from, to);
        double pad = Math.Max(1e-9, Math.Abs(from) * 1e-10);
        return (Math.Max(-double.MaxValue, from - pad), Math.Min(double.MaxValue, to + pad));
    }

    private static Func<int, double> ValueReader(IReadOnlyList<TestValue> values) => values switch
    {
        TestValueColumn column => column.GetValueAt,
        IndexedTestValues indexed => indexed.GetValueAt,
        _ => index => values[index].Value
    };

    private static double Percentile(double[] sorted, int count, double percentile)
    {
        double index = percentile * (count - 1);
        int lower = (int)index;
        double fraction = index - lower;
        return lower + 1 < count ? sorted[lower] * (1 - fraction) + sorted[lower + 1] * fraction : sorted[lower];
    }
}
