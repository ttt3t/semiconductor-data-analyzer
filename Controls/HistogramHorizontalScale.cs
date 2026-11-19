using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

internal sealed class HistogramHorizontalScale
{
    private readonly double _from, _to, _left, _width;
    private readonly double[]? _values, _positions;
    public double MinimumBarWidth { get; }
    public bool IsAdjusted => _values != null;

    internal HistogramHorizontalScale(double from, double to, double left, double width, IList<HistogramBin> bins)
    {
        _from = from;
        _to = to;
        _left = left;
        _width = width;
        if (bins.Count == 0) return;

        double span = to - from;
        double minimum = .5 / bins.Count;
        MinimumBarWidth = width * minimum;
        // Fix undersized in-spec slots at the minimum, then distribute the remaining
        // space proportionally. Integer gaps and adjacent bins retain their order.
        if (!bins.Any(bin => !bin.IsOutOfSpec && bin.To > bin.From && (bin.To - bin.From) / span < minimum))
            return;
        var candidates = bins.Where(bin => !bin.IsOutOfSpec && bin.To > bin.From)
            .Select(bin => (bin.To - bin.From) / span).Order().ToArray();
        double remainingSpace = 1, remainingSpan = 1, factor = 1;
        foreach (double candidate in candidates)
        {
            if (candidate * factor >= minimum) break;
            remainingSpace -= minimum;
            remainingSpan -= candidate;
            factor = remainingSpace / remainingSpan;
        }
        if (factor == 1) return;

        var values = new List<double>(2 * bins.Count + 2) { from };
        var positions = new List<double>(values.Capacity) { 0 };
        void Add(double value, double lowerBound)
        {
            if (value <= values[^1]) return;
            double proportionalWidth = (value - values[^1]) / span * factor;
            positions.Add(Math.Min(1, positions[^1] + Math.Max(proportionalWidth, lowerBound)));
            values.Add(value);
        }

        foreach (var bin in bins)
        {
            Add(bin.From, 0);
            Add(bin.To, bin.IsOutOfSpec ? 0 : minimum);
        }
        Add(to, 0);
        positions[^1] = 1;
        _values = values.ToArray();
        _positions = positions.ToArray();
    }

    internal double Map(double value) => _left + _width * (_values != null && _positions != null
        ? Interpolate(value, _values, _positions)
        : (value - _from) / (_to - _from));

    internal double Unmap(double position)
    {
        double fraction = (position - _left) / _width;
        return _positions != null && _values != null
            ? Interpolate(fraction, _positions, _values)
            : _from + fraction * (_to - _from);
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
