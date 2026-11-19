using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

internal sealed class HistogramHorizontalScale
{
    private readonly double _from, _domainFrom, _domainTo, _span, _left, _width;
    private readonly double[]? _values, _positions;
    public double MinimumBarWidth { get; }
    internal bool IntegerBins { get; }
    public bool IsAdjusted => _values != null;

    internal HistogramHorizontalScale(double from, double to, double left, double width, IList<HistogramBin> bins,
        bool integerBins = false)
    {
        _from = from;
        _span = to - from;
        _left = left;
        _width = width;
        IntegerBins = integerBins;
        _domainFrom = DomainValue(from);
        _domainTo = DomainValue(to);
        if (bins.Count == 0) return;

        double minimum = .5 / bins.Count;
        MinimumBarWidth = width * minimum;
        // Fix undersized in-spec slots at the minimum, then distribute the remaining
        // space proportionally. The plotted extents include the full integer cells.
        double WidthFraction(HistogramBin bin)
        {
            var (start, end) = BinExtent(bin);
            return (end - start) / _span;
        }
        bool NeedsMoreWidth(HistogramBin bin)
        {
            if (bin.IsOutOfSpec) return false;
            double fraction = WidthFraction(bin);
            return fraction > 0 && fraction < minimum;
        }
        if (!bins.Any(NeedsMoreWidth)) return;
        var candidates = bins.Where(bin => !bin.IsOutOfSpec).Select(WidthFraction).Where(fraction => fraction > 0).Order();
        double remainingSpace = 1, remainingSpan = 1, factor = 1;
        foreach (double candidate in candidates)
        {
            if (candidate * factor >= minimum) break;
            remainingSpace -= minimum;
            remainingSpan -= candidate;
            factor = remainingSpace / remainingSpan;
        }
        if (factor == 1) return;

        var values = new List<double>(2 * bins.Count + 2) { _domainFrom };
        var positions = new List<double>(values.Capacity) { 0 };
        void Add(double value, double lowerBound)
        {
            if (value <= values[^1]) return;
            double proportionalWidth = (value - values[^1]) / _span * factor;
            positions.Add(Math.Min(1, positions[^1] + Math.Max(proportionalWidth, lowerBound)));
            values.Add(value);
        }

        foreach (var bin in bins)
        {
            var (start, end) = BinExtent(bin);
            Add(start, 0);
            Add(end, bin.IsOutOfSpec ? 0 : minimum);
        }
        Add(_domainTo, 0);
        positions[^1] = 1;
        _values = values.ToArray();
        _positions = positions.ToArray();
    }

    // Use offsets for integer cells so half steps remain representable even near large integer values.
    private double DomainValue(double value) => IntegerBins ? value - _from : value;

    private (double From, double To) BinExtent(HistogramBin bin) => IntegerBins
        ? (Math.Max(_domainFrom, DomainValue(bin.From) - .5), Math.Min(_domainTo, DomainValue(bin.To) + .5))
        : (bin.From, bin.To);

    private double MapDomain(double value) => _left + _width * (_values != null && _positions != null
        ? Interpolate(value, _values, _positions)
        : (value - _domainFrom) / _span);

    internal double Map(double value) => MapDomain(DomainValue(value));

    internal double? ReferencePosition(double? value, double? firstIntegerAfter = null)
    {
        if (value is not double limit || !double.IsFinite(limit)) return null;
        double position = firstIntegerAfter is double cut ? DomainValue(cut) - .5 : DomainValue(limit);
        return position >= _domainFrom && position <= _domainTo ? MapDomain(position) : null;
    }

    internal (double Left, double Right) MapBin(HistogramBin bin)
    {
        var (start, end) = BinExtent(bin);
        return (MapDomain(start), MapDomain(end));
    }

    internal HistogramBin? HitIntegerBin(IList<HistogramBin> bins, double x)
    {
        int first = 0, last = bins.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            var (_, right) = MapBin(bins[middle]);
            if (x > right || x == right && middle < bins.Count - 1) first = middle + 1;
            else last = middle;
        }
        return first < bins.Count && x >= MapBin(bins[first]).Left ? bins[first] : null;
    }

    internal double Unmap(double position)
    {
        double fraction = (position - _left) / _width;
        double value = _positions != null && _values != null
            ? Interpolate(fraction, _positions, _values)
            : _domainFrom + fraction * _span;
        return IntegerBins ? _from + value : value;
    }

    private static double Interpolate(double value, double[] domain, double[] range)
    {
        int index = Array.BinarySearch(domain, value);
        if (index >= 0) return range[index];
        int upper = ~index;
        if (upper == 0) return range[0];
        if (upper == domain.Length) return range[^1];
        int lower = upper - 1;
        double fraction = (value - domain[lower]) / (domain[upper] - domain[lower]);
        return range[lower] + fraction * (range[upper] - range[lower]);
    }
}
