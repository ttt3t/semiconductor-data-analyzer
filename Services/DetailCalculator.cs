using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record DetailRequest(TestItemData Item, string? Site, int BinMode, int BinCount,
    bool IncludeOutOfLimit, bool ShowLsl, bool ShowUsl, bool ShowMean, bool ShowThreeSigma);

public sealed record DetailResult(StatisticsResult Statistics, List<HistogramBin> Bins,
    double Mean, double Sigma, ScatterSeries Scatter);

internal sealed record PreparedDetail(IReadOnlyList<TestValue> Values, StatisticsResult Statistics, ScatterSeries Scatter);

public static class DetailCalculator
{
    public static DetailResult Calculate(DetailRequest request, CancellationToken token)
        => CreateHistogram(request, Prepare(request, token), token);

    internal static PreparedDetail Prepare(DetailRequest request, CancellationToken token)
    {
        var item = request.Item;
        IReadOnlyList<TestValue> values = item.Values;
        if (request.Site != null)
        {
            var filtered = new List<int>();
            for (int i = 0; i < item.Values.Count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var value = item.Values[i];
                if ((string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site) == request.Site)
                    filtered.Add(i);
            }
            values = new IndexedTestValues(item.Values, filtered);
        }
        token.ThrowIfCancellationRequested();
        var statistics = StatisticsService.Calculate(values, item.LowLimit, item.HighLimit);
        token.ThrowIfCancellationRequested();
        var scatter = ScatterSeries.Build(values, item.LowLimit, item.HighLimit, token);
        return new(values, statistics, scatter);
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
                request.IncludeOutOfLimit, out mean, out sigma);
        else if (request.BinMode == 1 && item.LowLimit.HasValue && item.HighLimit > item.LowLimit)
            bins = HistogramService.CreateSpecZoneBins(values, item.LowLimit.Value, item.HighLimit.Value,
                request.IncludeOutOfLimit);
        else
            bins = HistogramService.CreateFixedBins(values, request.BinCount, item.LowLimit,
                item.HighLimit, request.IncludeOutOfLimit);
        token.ThrowIfCancellationRequested();
        return new(statistics, bins, mean, sigma, prepared.Scatter);
    }
}

/// <summary>Accessed by the single detail worker. Retains only the current item/site and three modes.</summary>
public sealed class DetailCache
{
    private (TestItemData Item, string? Site, double? Lsl, double? Usl, int Count)? _key;
    private PreparedDetail? _prepared;
    private readonly Dictionary<(int Mode, int Count, bool IncludeOutOfLimit), DetailResult> _modes = new();

    public DetailResult Calculate(DetailRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (request.Item, request.Site, request.Item.LowLimit, request.Item.HighLimit, request.Item.Values.Count);
        if (_key != key || _prepared == null)
        {
            _prepared = null;
            _modes.Clear();
            _key = null;
            var prepared = DetailCalculator.Prepare(request, token);
            token.ThrowIfCancellationRequested();
            _prepared = prepared;
            _key = key;
        }
        var modeKey = (request.BinMode, request.BinCount, request.IncludeOutOfLimit);
        if (_modes.TryGetValue(modeKey, out var existing)) return existing;
        var result = DetailCalculator.CreateHistogram(request, _prepared, token);
        token.ThrowIfCancellationRequested();
        if (_modes.Count >= 3) _modes.Clear();
        _modes[modeKey] = result;
        return result;
    }
}
