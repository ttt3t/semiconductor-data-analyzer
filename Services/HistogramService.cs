using System.Buffers;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class HistogramBin
{
    public double From { get; set; }
    public double To { get; set; }
    public int Count { get; set; }
    public bool IsOutOfSpec { get; init; }
    public bool IncludesFrom { get; init; } = true;
    public bool IncludesTo { get; init; }
}

public readonly record struct HistogramView(double From, double To, int Below, int Above, int RequestedBins)
{
    internal HistogramIntegerLimits? IntegerLimits { get; init; }
}
internal sealed record HistogramDistribution(List<HistogramBin> Bins, HistogramView? View);
internal readonly record struct HistogramIntegerLimits(double FirstAllowed, double FirstAbove);

public static class HistogramService
{
    // Every integer in this range and the next endpoint are representable as doubles.
    private const double LargestExactInteger = 9_007_199_254_740_991;

    private readonly record struct Section(double From, double To, bool IsOutOfSpec, bool IncludesFrom, bool IncludesTo)
    {
        public long IntegerPositions => (long)To - (long)From + 1;
        public double Weight(bool integers) => integers ? IntegerPositions : To - From;
        public int Capacity(bool integers) => integers ? (int)Math.Min(int.MaxValue, IntegerPositions)
            : From == To ? 1 : int.MaxValue;
    }

    public static List<HistogramBin> CreateFixedBins(
        IReadOnlyList<TestValue> values, int binCount, double? lsl, double? usl, bool includeOutOfLimit,
        CancellationToken token = default, double? integerBinFactor = null)
    {
        var range = FixedRange(values, lsl, usl, includeOutOfLimit, token);
        return CreateRangeBins(values, binCount, range.From, range.To, lsl, usl, includeOutOfLimit,
            integerBinFactor is double factor ? FindIntegerLimits(values, factor, lsl, usl, token) : null,
            integerBinFactor ?? 1, token).Bins;
    }

    public static List<HistogramBin> CreateSpecZoneBins(
        IReadOnlyList<TestValue> values, double lsl, double usl, bool includeOutOfLimit,
        int binCount = AnalyzerConfig.DefaultSpecZoneBinCount, CancellationToken token = default, double? integerBinFactor = null)
    {
        if (usl <= lsl) return new();
        var range = SpecRange(lsl, usl);
        return CreateRangeBins(values, binCount, range.From, range.To, lsl, usl, includeOutOfLimit,
            integerBinFactor is double factor ? FindIntegerLimits(values, factor, lsl, usl, token) : null,
            integerBinFactor ?? 1, token).Bins;
    }

    public static List<HistogramBin> CreateMeanCenteredBins(
        IReadOnlyList<TestValue> values, double? lsl, double? usl, bool includeOutOfLimit,
        out double center, out double scale, int binCount = AnalyzerConfig.DefaultMedianBinCount,
        CancellationToken token = default, double? integerBinFactor = null)
    {
        var range = MedianRange(values, token, out center, out scale);
        return CreateRangeBins(values, binCount, range.From, range.To, lsl, usl, includeOutOfLimit,
            integerBinFactor is double factor ? FindIntegerLimits(values, factor, lsl, usl, token) : null,
            integerBinFactor ?? 1, token).Bins;
    }

    internal static (double From, double To) FixedRange(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        bool includeOutOfLimit, CancellationToken token)
    {
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
        return PadConstant(min, max);
    }

    internal static (double From, double To) SpecRange(double lsl, double usl)
    {
        double tail = (usl - lsl) * (4.0 / 7.0);
        return (lsl - tail, usl + tail);
    }

    internal static (double From, double To) MedianRange(IReadOnlyList<TestValue> values, CancellationToken token,
        out double center, out double scale)
    {
        center = scale = 0;
        if (values.Count == 0) return (double.NaN, double.NaN);

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
            if (count == 0) return (double.NaN, double.NaN);
            Array.Sort(sorted, 0, count);
            token.ThrowIfCancellationRequested();
            center = Percentile(sorted, count, 0.5);
            scale = (Percentile(sorted, count, 0.75) - Percentile(sorted, count, 0.25)) / 1.349;
        }
        finally { ArrayPool<double>.Shared.Return(sorted); }
        return PadConstant(center - 8 * scale, center + 8 * scale);
    }

    internal static HistogramDistribution CreateRangeBins(
        IReadOnlyList<TestValue> values, int requested, double from, double to, double? lsl, double? usl,
        bool includeOutOfLimit, HistogramIntegerLimits? integerLimits, double factor, CancellationToken token)
    {
        double plotFrom = from * factor, plotTo = to * factor, plotSpan = plotTo - plotFrom;
        if (values.Count == 0 || requested < 1 || !double.IsFinite(plotSpan) || plotSpan <= 0)
            return new(new(), null);

        bool integerBins = integerLimits.HasValue;
        var sections = integerLimits is { } limits ? IntegerSections(plotFrom, plotTo, limits)
            : ContinuousSections(from, to, lsl, usl);
        var bins = DivideSections(sections, requested, integerBins, token);

        int below = 0, above = 0;
        var read = ValueReader(values);
        double lowerView = integerBins ? plotFrom : from, upperView = integerBins ? plotTo : to;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value) || (!includeOutOfLimit && Outside(value, lsl, usl))) continue;
            double position = integerBins ? value * factor : value;
            if (position < lowerView) below++;
            else if (position > upperView) above++;
            else
            {
                int index = FindBin(bins, position);
                if (index >= 0) bins[index].Count++;
            }
        }

        if (!integerBins && factor != 1)
            foreach (var bin in bins) { bin.From *= factor; bin.To *= factor; }
        return new(bins, new HistogramView(plotFrom, plotTo, below, above, requested) { IntegerLimits = integerLimits });
    }

    private static List<Section> ContinuousSections(double from, double to, double? lsl, double? usl)
    {
        Span<double> edges = stackalloc double[4];
        int count = 0;
        edges[count++] = from;
        if (lsl is double low && low > from && low < to) edges[count++] = low;
        if (usl is double high && high > from && high < to && high != lsl) edges[count++] = high;
        edges[count++] = to;
        edges[..count].Sort();

        var sections = new List<Section>(count);
        bool previousIncludesEnd = false;
        for (int index = 0; index < count - 1; index++)
        {
            double lower = edges[index], upper = edges[index + 1];
            bool outside = OutsideInterval(lower, upper, lsl, usl);
            bool lowerBelongsHere = Outside(lower, lsl, usl) == outside;
            if (!lowerBelongsHere && !previousIncludesEnd)
                sections.Add(new(lower, lower, !outside, true, true));

            bool upperOutside = Outside(upper, lsl, usl);
            bool last = index + 1 == count - 1;
            bool belongsOnRight = !last && upperOutside == OutsideInterval(upper, edges[index + 2], lsl, usl);
            bool includesEnd = !belongsOnRight && upperOutside == outside;
            sections.Add(new(lower, upper, outside, lowerBelongsHere, includesEnd));
            if (last && !includesEnd) sections.Add(new(upper, upper, upperOutside, true, true));
            previousIncludesEnd = includesEnd;
        }
        return sections;
    }

    private static bool Outside(double value, double? lsl, double? usl) => value < lsl || value > usl;

    private static bool OutsideInterval(double from, double to, double? lsl, double? usl) => to <= lsl || from >= usl;

    // STDF's exclusive limits already have their effective one-ULP adjustments.
    // When multiplication rounds an adjusted threshold to an integer, the residual
    // still indicates which side contains that integer.
    private static HistogramIntegerLimits IntegerCuts(double? lsl, double? usl, double factor)
    {
        double firstAllowed = double.NegativeInfinity, firstAbove = double.PositiveInfinity;
        if (lsl is double low)
        {
            double plotted = low * factor;
            firstAllowed = Math.Ceiling(plotted);
            if (double.IsInteger(plotted) && Math.FusedMultiplyAdd(low, factor, -plotted) > 0)
                firstAllowed++;
        }
        if (usl is double high)
        {
            double plotted = high * factor;
            firstAbove = Math.Floor(plotted) + 1;
            if (double.IsInteger(plotted) && Math.FusedMultiplyAdd(high, factor, -plotted) < 0)
                firstAbove = plotted;
        }
        return new(firstAllowed, firstAbove);
    }

    private static List<Section> IntegerSections(double from, double to, HistogramIntegerLimits limits)
    {
        double first = Math.Max(-LargestExactInteger, Math.Ceiling(from));
        double last = Math.Min(LargestExactInteger, Math.Floor(to));
        var sections = new List<Section>(3);
        if (last < first) return sections;

        var (firstAllowed, firstAbove) = limits;
        Span<double> edges = stackalloc double[4];
        int count = 0;
        edges[count++] = first;
        if (firstAllowed > first && firstAllowed <= last) edges[count++] = firstAllowed;
        if (firstAbove > first && firstAbove <= last && firstAbove != firstAllowed) edges[count++] = firstAbove;
        edges[count++] = last + 1;
        edges[..count].Sort();

        for (int i = 0; i < count - 1; i++)
        {
            double lower = edges[i];
            sections.Add(new(lower, edges[i + 1] - 1, lower < firstAllowed || lower >= firstAbove, true, true));
        }
        return sections;
    }

    private static List<HistogramBin> DivideSections(List<Section> sections, int requested, bool integers, CancellationToken token)
    {
        int remaining = (int)Math.Min(Math.Max(requested, sections.Count), sections.Sum(s => (long)s.Capacity(integers)));
        var bins = new List<HistogramBin>(remaining);
        double remainingWeight = sections.Sum(s => s.Weight(integers));
        long remainingCapacity = sections.Sum(s => (long)s.Capacity(integers));
        for (int index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            int capacity = section.Capacity(integers);
            double weight = section.Weight(integers);
            remainingCapacity -= capacity;
            int nextSections = sections.Count - index - 1;
            int minimum = (int)Math.Max(1, remaining - remainingCapacity);
            int maximum = Math.Min(capacity, remaining - nextSections);
            int count = remainingWeight > 0 ?
                Math.Clamp((int)Math.Round(weight / remainingWeight * remaining), minimum, maximum) : minimum;
            double start = section.From;
            for (int i = 1; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                // In integer mode even a one-ULP error before flooring can merge two positions.
                double upper = integers ? (long)section.From + (long)((Int128)section.IntegerPositions * i / count)
                    : section.From + weight * ((double)i / count);
                if (!integers && (upper <= start || upper >= section.To)) continue;
                bins.Add(new HistogramBin
                {
                    From = start, To = integers ? upper - 1 : upper, IsOutOfSpec = section.IsOutOfSpec,
                    IncludesFrom = start != section.From || section.IncludesFrom, IncludesTo = integers
                });
                start = upper;
            }
            bins.Add(new HistogramBin
            {
                From = start, To = section.To, IsOutOfSpec = section.IsOutOfSpec,
                IncludesFrom = start != section.From || section.IncludesFrom, IncludesTo = section.IncludesTo
            });
            remaining -= count;
            remainingWeight -= weight;
        }
        return bins;
    }

    internal static int FindBin(IList<HistogramBin> bins, double position)
    {
        int first = 0, last = bins.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            var bin = bins[middle];
            if (position > bin.To || position == bin.To && !bin.IncludesTo) first = middle + 1;
            else last = middle;
        }
        if (first == bins.Count) return -1;
        var candidate = bins[first];
        return position > candidate.From || position == candidate.From && candidate.IncludesFrom ? first : -1;
    }

    internal static HistogramIntegerLimits? FindIntegerLimits(
        IReadOnlyList<TestValue> values, double factor, double? lsl, double? usl, CancellationToken token)
    {
        var (firstAllowed, firstAbove) = IntegerCuts(lsl, usl, factor);
        double? plottedLow = lsl * factor, plottedHigh = usl * factor;
        bool? belowAtLow = null, aboveAtHigh = null;
        var read = ValueReader(values);
        bool any = false;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = read(i);
            if (!double.IsFinite(value)) continue;
            double plotted = value * factor;
            if (!double.IsInteger(plotted) || Math.Abs(plotted) > LargestExactInteger) return null;
            any = true;
            // Preserve the raw comparison when the displayed boundary and sample round to the
            // same integer. If that integer has conflicting outcomes, keep raw coordinates.
            if (plotted == plottedLow)
            {
                bool below = value < lsl;
                if (belowAtLow.HasValue && belowAtLow.Value != below) return null;
                belowAtLow = below;
                firstAllowed = below ? plotted + 1 : plotted;
            }
            if (plotted == plottedHigh)
            {
                bool above = value > usl;
                if (aboveAtHigh.HasValue && aboveAtHigh.Value != above) return null;
                aboveAtHigh = above;
                firstAbove = above ? plotted : plotted + 1;
            }
        }
        return any ? new(firstAllowed, firstAbove) : null;
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
