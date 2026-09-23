using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public readonly record struct ScatterPoint(int Index, double Value, bool OutOfSpec);

/// <summary>Every finite measurement in acquisition order. No sampled or aggregated levels.</summary>
public sealed class ScatterSeries
{
    public static ScatterSeries Empty { get; } = new();
    public ScatterPoint[] Points { get; private init; } = Array.Empty<ScatterPoint>();
    public int SampleCount { get; private init; }
    public double Minimum { get; private init; }
    public double Maximum { get; private init; }

    public static ScatterSeries Build(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var points = new ScatterPoint[values.Count];
        int count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            double value = values[i].Value;
            if (!double.IsFinite(value)) continue;
            points[count++] = new(i + 1, value, (lsl.HasValue && value < lsl) || (usl.HasValue && value > usl));
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        if (count == 0) return Empty;
        if (count != points.Length) Array.Resize(ref points, count);
        return new ScatterSeries { Points = points, SampleCount = values.Count, Minimum = min, Maximum = max };
    }

    public static int LowerBound(ScatterPoint[] points, double index)
    {
        int lo = 0, hi = points.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (points[mid].Index < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}