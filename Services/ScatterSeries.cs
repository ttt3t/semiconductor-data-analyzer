using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public readonly record struct ScatterPoint(int Index, double Value, bool OutOfSpec)
{
    // Fits the existing struct padding on x64; no per-point identity objects.
    public int SourceIndex { get; init; }
}
public readonly record struct ScatterGroup(string Name, int StartIndex, int EndIndex);

/// <summary>Every finite measurement. No sampled or aggregated levels.</summary>
public sealed class ScatterSeries
{
    public static ScatterSeries Empty { get; } = new();
    public ScatterPoint[] Points { get; private init; } = Array.Empty<ScatterPoint>();
    public int SampleCount { get; private init; }
    public double Minimum { get; private init; }
    public double Maximum { get; private init; }
    public ScatterOrder Order { get; private init; } = ScatterOrder.SerialNumber;
    public ScatterGroup[] Groups { get; private init; } = Array.Empty<ScatterGroup>();
    private IReadOnlyList<TestValue>? _identityValues;
    internal bool HasChipIdentities => _identityValues != null;
    internal CsvRecord? RecordAtPoint(int index)
    {
        int row = Points[index].SourceIndex;
        return _identityValues switch
        {
            TestValueColumn column => column.GetRecordAt(row),
            IndexedTestValues selected => selected.GetRecordAt(row),
            null => null,
            _ => _identityValues[row].Record
        };
    }

    public ScatterSeries Scale(double factor) => factor == 1 ? this : new ScatterSeries
    {
        Points = Points.Select(p => p with { Value = p.Value * factor }).ToArray(),
        SampleCount = SampleCount, Minimum = Minimum * factor, Maximum = Maximum * factor,
        Order = Order, Groups = Groups, _identityValues = _identityValues
    };

    private readonly record struct SortKey(int SourceIndex, int Wafer, double SiteNumber, string Site,
        double SerialNumber, string Serial);

    public static ScatterSeries BuildSorted(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        ScatterOrder order, CancellationToken token = default, IReadOnlyList<string>? waferOrder = null, bool trackChips = false)
    {
        token.ThrowIfCancellationRequested();
        var keys = new SortKey[values.Count];
        var wafers = new Dictionary<string, int>(StringComparer.Ordinal);
        if (waferOrder != null)
            foreach (string wafer in waferOrder) wafers.TryAdd(wafer, wafers.Count);
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var value = values[i];
            if (!wafers.TryGetValue(value.WaferId, out int wafer))
                wafers[value.WaferId] = wafer = wafers.Count;
            string site = string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site;
            keys[i] = new(i, wafer, NumericText.SortKey(site), site,
                NumericText.SortKey(value.SerialNumber), value.SerialNumber);
        }
        int comparisons = 0;
        try
        {
            Array.Sort(keys, (a, b) =>
            {
                if ((++comparisons & 4095) == 0) token.ThrowIfCancellationRequested();
                int result = 0;
                if (order == ScatterOrder.Site)
                {
                    result = a.SiteNumber.CompareTo(b.SiteNumber);
                    if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.Site, b.Site);
                    if (result == 0) result = StringComparer.Ordinal.Compare(a.Site, b.Site);
                }
                else if (order == ScatterOrder.Wafer) result = a.Wafer.CompareTo(b.Wafer);
                if (result == 0) result = a.SerialNumber.CompareTo(b.SerialNumber);
                if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.Serial, b.Serial);
                // Stable ties retain acquisition order, including repeated test records.
                return result != 0 ? result : a.SourceIndex.CompareTo(b.SourceIndex);
            });
        }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
        token.ThrowIfCancellationRequested();
        var indices = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++) indices[i] = keys[i].SourceIndex;
        var series = BuildCore(values, lsl, usl, token, indices, trackChips);
        if (order == ScatterOrder.SerialNumber) return series;
        if (series.Points.Length == 0) return new ScatterSeries { Order = order };

        // Only one small descriptor per contiguous group; measurement points retain their compact layout.
        var groups = new List<ScatterGroup>();
        int start = 0;
        for (int i = 1; i <= keys.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (i < keys.Length && (order == ScatterOrder.Site
                ? keys[i].Site == keys[start].Site : keys[i].Wafer == keys[start].Wafer)) continue;
            string name = order == ScatterOrder.Site ? keys[start].Site : values[keys[start].SourceIndex].WaferId;
            groups.Add(new(string.IsNullOrWhiteSpace(name) ? "(空)" : name, start + 1, i));
            start = i;
        }
        token.ThrowIfCancellationRequested();
        return new ScatterSeries { Points = series.Points, SampleCount = series.SampleCount,
            Minimum = series.Minimum, Maximum = series.Maximum, Order = order, Groups = groups.ToArray(),
            _identityValues = series._identityValues };
    }

    public static ScatterSeries Build(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        CancellationToken cancellationToken = default, bool trackChips = false)
        => BuildCore(values, lsl, usl, cancellationToken, null, trackChips);

    private static ScatterSeries BuildCore(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        CancellationToken cancellationToken, int[]? indices, bool trackChips)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var points = new ScatterPoint[values.Count];
        int count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = indices == null ? i : indices[i];
            double value = values[sourceIndex].Value;
            if (!double.IsFinite(value)) continue;
            points[count++] = new(i + 1, value, (lsl.HasValue && value < lsl) || (usl.HasValue && value > usl))
                { SourceIndex = sourceIndex };
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        if (count == 0) return Empty;
        if (count != points.Length) Array.Resize(ref points, count);
        return new ScatterSeries { Points = points, SampleCount = values.Count, Minimum = min, Maximum = max,
            _identityValues = trackChips ? values : null };
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

    public static int LowerBoundGroup(ScatterGroup[] groups, double index)
    {
        int lo = 0, hi = groups.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (groups[mid].EndIndex + 0.5 < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
