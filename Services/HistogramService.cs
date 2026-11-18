using System.Buffers;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class HistogramBin
{
    public double From { get; set; }
    public double To { get; set; }
    public int Count { get; set; }
    public bool IsOutOfSpec { get; set; }
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
        CancellationToken token = default)
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
        return CreateRangeBins(values, binCount, min, max, lsl, usl, includeOutOfLimit, false, true, token);
    }

    public static List<HistogramBin> CreateSpecZoneBins(
        IReadOnlyList<TestValue> values, double lsl, double usl, bool includeOutOfLimit,
        int binCount = AnalyzerConfig.DefaultSpecZoneBinCount, CancellationToken token = default)
    {
        if (values.Count == 0 || usl <= lsl) return new();
        // Preserve the original spec range: each tail covers 40/70 of the distance between the limits.
        double tail = (usl - lsl) * (4.0 / 7.0);
        return CreateRangeBins(values, binCount, lsl - tail, usl + tail, lsl, usl, includeOutOfLimit, true, true, token);
    }

    public static List<HistogramBin> CreateMeanCenteredBins(
        IReadOnlyList<TestValue> values, double? lsl, double? usl, bool includeOutOfLimit,
        out double center, out double scale, int binCount = AnalyzerConfig.DefaultMedianBinCount,
        CancellationToken token = default)
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
        return CreateRangeBins(values, binCount, from, to, lsl, usl, includeOutOfLimit, false, true, token);
    }

    internal static List<HistogramBin> CreateRangeBins(
        IReadOnlyList<TestValue> values, int binCount, double from, double to, double? lsl, double? usl,
        bool includeOutOfLimit, bool splitAtLimits, bool includeOverflow, CancellationToken token)
    {
        double span = to - from;
        if (values.Count == 0 || binCount < 1 || !double.IsFinite(span) || !(span / binCount > 0)) return new();

        Span<double> edges = stackalloc double[4];
        int edgeCount = 0;
        edges[edgeCount++] = from;
        if (splitAtLimits && lsl is double low && usl is double high && high > low)
        {
            if (low > from && low < to) edges[edgeCount++] = low;
            if (high > from && high < to) edges[edgeCount++] = high;
        }
        if (edgeCount > binCount) edgeCount = 1;
        edges[edgeCount++] = to;

        Span<BinSection> sections = stackalloc BinSection[3];
        var bins = new List<HistogramBin>(binCount);
        for (int index = 1; index < edgeCount; index++)
        {
            int end = index == edgeCount - 1 ? binCount :
                Math.Clamp((int)Math.Round((edges[index] - from) / span * binCount, MidpointRounding.AwayFromZero),
                    bins.Count + 1, binCount - (edgeCount - 1 - index));
            BinSection section = new(edges[index - 1], edges[index], bins.Count, end - bins.Count);
            if (!(section.Width > 0)) return new();
            sections[index - 1] = section;
            double lower = section.From;
            for (int bin = 0; bin < section.Count; bin++)
            {
                if ((bin & 4095) == 0) token.ThrowIfCancellationRequested();
                double upper = bin == section.Count - 1 ? section.To :
                    Math.Min(section.To, section.From + (bin + 1) * section.Width);
                bins.Add(new HistogramBin
                {
                    From = lower,
                    To = upper,
                    IsOutOfSpec = (lsl.HasValue && upper <= lsl.Value) || (usl.HasValue && lower >= usl.Value)
                });
                lower = upper;
            }
        }

        var read = ValueReader(values);
        int sectionCount = edgeCount - 1;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value) ||
                (!includeOutOfLimit && (value < lsl || value > usl)) ||
                (!includeOverflow && (value < from || value > to))) continue;
            int section = 0;
            while (section + 1 < sectionCount &&
                (value > sections[section].To || value == sections[section].To && value != usl))
                section++;
            bins[sections[section].Index(value)].Count++;
        }
        return bins;
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
