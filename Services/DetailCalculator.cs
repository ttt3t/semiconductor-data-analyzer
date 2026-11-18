using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record DetailRequest(TestItemData Item, string? Site, int BinMode, int BinCount,
    bool IncludeOutOfLimit, bool ShowLsl, bool ShowUsl, bool ShowMean, bool ShowThreeSigma,
    string? Wafer = null, ScatterOrder ScatterOrder = ScatterOrder.SerialNumber,
    IReadOnlyList<string>? WaferOrder = null, double HistogramRangeScale = 1);

public sealed record DetailResult(StatisticsResult Statistics, List<HistogramBin> Bins,
    double Mean, double Sigma, ScatterSeries Scatter)
{
    internal (double From, double To)? RawHistogramRange { get; init; }
    internal bool IsHistogramZoomed { get; init; }
}

internal sealed record PreparedDetail(IReadOnlyList<TestValue> Values, StatisticsResult Statistics, ScatterSeries Scatter);

public static class DetailCalculator
{
    public static DetailResult Calculate(DetailRequest request, CancellationToken token)
    {
        var prepared = Prepare(request, token);
        var original = CreateHistogram(request, prepared, token);
        return ZoomHistogram(request, prepared, original, token);
    }

    internal static PreparedDetail Prepare(DetailRequest request, CancellationToken token)
    {
        var item = request.Item;
        IReadOnlyList<TestValue> values = item.Values;
        if (request.Site != null || request.Wafer != null)
        {
            var filtered = new List<int>();
            for (int i = 0; i < item.Values.Count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var value = item.Values[i];
                if ((request.Site == null || (string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site) == request.Site) &&
                    (request.Wafer == null || value.WaferId == request.Wafer))
                    filtered.Add(i);
            }
            values = new IndexedTestValues(item.Values, filtered);
        }
        token.ThrowIfCancellationRequested();
        var statistics = StatisticsService.Calculate(values, item.LowLimit, item.HighLimit);
        token.ThrowIfCancellationRequested();
        var scatter = ScatterSeries.BuildSorted(values, item.LowLimit, item.HighLimit, request.ScatterOrder, token, request.WaferOrder, trackChips: true);
        return new(values, statistics, scatter.Scale(item.DisplayFactor));
    }

    internal static DetailResult CreateHistogram(DetailRequest request, PreparedDetail prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var item = request.Item;
        var values = prepared.Values;
        var statistics = prepared.Statistics;
        double mean = statistics.Mean, sigma = statistics.StdDev;
        List<HistogramBin> bins;
        if (request.BinMode == 2)
            bins = HistogramService.CreateMeanCenteredBins(values, item.LowLimit, item.HighLimit,
                request.IncludeOutOfLimit, out mean, out sigma, request.BinCount, token);
        else if (UsesSpecZones(request))
            bins = HistogramService.CreateSpecZoneBins(values, item.LowLimit!.Value, item.HighLimit!.Value,
                request.IncludeOutOfLimit, request.BinCount, token);
        else
            bins = HistogramService.CreateFixedBins(values, request.BinCount, item.LowLimit,
                item.HighLimit, request.IncludeOutOfLimit, token);
        token.ThrowIfCancellationRequested();
        double factor = item.DisplayFactor;
        var rawRange = bins.Count == 0 ? ((double, double)?)null : (bins[0].From, bins[^1].To);
        ScaleBins(bins, factor);
        return new(statistics, bins, mean * factor, sigma * factor, prepared.Scatter) { RawHistogramRange = rawRange };
    }

    internal static DetailResult ZoomHistogram(DetailRequest request, PreparedDetail prepared, DetailResult original,
        CancellationToken token)
    {
        double scale = request.HistogramRangeScale;
        if (scale == 1 || !double.IsFinite(scale) || scale <= 0 || original.RawHistogramRange is not { } range)
            return original;
        double halfSpan = (range.To - range.From) / 2;
        double center = range.From + halfSpan;
        double radius = halfSpan * scale;
        var item = request.Item;
        var bins = HistogramService.CreateRangeBins(prepared.Values, request.BinCount, center - radius, center + radius,
            item.LowLimit, item.HighLimit, request.IncludeOutOfLimit, UsesSpecZones(request), false, token);
        if (bins.Count == 0) return original;
        ScaleBins(bins, item.DisplayFactor);
        return original with { Bins = bins, IsHistogramZoomed = true };
    }

    private static bool UsesSpecZones(DetailRequest request) =>
        request.BinMode == 1 && request.Item.LowLimit is double lsl && double.IsFinite(lsl) &&
        request.Item.HighLimit is double usl && double.IsFinite(usl) && usl > lsl;

    private static void ScaleBins(List<HistogramBin> bins, double factor)
    {
        if (factor == 1) return;
        foreach (var bin in bins) { bin.From *= factor; bin.To *= factor; }
    }
}

/// <summary>Accessed by the single detail worker. Retains one item/scope/order and three histogram modes.</summary>
public sealed class DetailCache
{
    private (TestItemData Item, string? Site, string? Wafer, double? Lsl, double? Usl, int Count)? _key;
    private ScatterOrder _order;
    private string[]? _waferOrder;
    private PreparedDetail? _prepared;
    private readonly Dictionary<(int Mode, int Count, bool IncludeOutOfLimit), DetailResult> _modes = new();

    public DetailResult Calculate(DetailRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (request.Item, request.Site, request.Wafer, request.Item.LowLimit, request.Item.HighLimit, request.Item.Values.Count);
        if (_key != key || _prepared == null)
        {
            _prepared = null;
            _modes.Clear();
            _key = null;
            var prepared = DetailCalculator.Prepare(request, token);
            token.ThrowIfCancellationRequested();
            _prepared = prepared;
            _order = request.ScatterOrder;
            _waferOrder = request.WaferOrder?.ToArray();
            _key = key;
        }
        else if (_order != request.ScatterOrder || request.ScatterOrder == ScatterOrder.Wafer &&
            !(_waferOrder == null ? request.WaferOrder == null : request.WaferOrder != null && _waferOrder.SequenceEqual(request.WaferOrder)))
        {
            var scatter = ScatterSeries.BuildSorted(_prepared.Values, request.Item.LowLimit,
                request.Item.HighLimit, request.ScatterOrder, token, request.WaferOrder, trackChips: true).Scale(request.Item.DisplayFactor);
            token.ThrowIfCancellationRequested();
            _prepared = _prepared with { Scatter = scatter };
            foreach (var cached in _modes.Keys.ToArray()) _modes[cached] = _modes[cached] with { Scatter = scatter };
            _order = request.ScatterOrder;
            _waferOrder = request.WaferOrder?.ToArray();
        }
        var modeKey = (request.BinMode, request.BinCount, request.IncludeOutOfLimit);
        if (!_modes.TryGetValue(modeKey, out var original))
        {
            original = DetailCalculator.CreateHistogram(request, _prepared, token);
            token.ThrowIfCancellationRequested();
            if (_modes.Count >= 3) _modes.Clear();
            _modes[modeKey] = original;
        }
        return DetailCalculator.ZoomHistogram(request, _prepared, original, token);
    }
}
