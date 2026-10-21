using System.Runtime.InteropServices;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class SummaryService
{
    public static SummaryRows Build(IReadOnlyList<TestItemData> items, IReadOnlyList<string> sites,
        bool combineSites, AnalyzerConfig config)
        => Build(items, sites, Array.Empty<string>(), combineSites, true, config);

    public static SummaryRows Build(IReadOnlyList<TestItemData> items, IReadOnlyList<string> sites,
        IReadOnlyList<string> wafers, bool combineSites, bool combineWafers, AnalyzerConfig config)
    {
        AnalysisOperation.CheckCancellation();
        int itemCount = items.Count;
        if (itemCount == 0) return new SummaryRows();
        string format = $"F{config.DecimalPlaces}";

        if (combineSites && combineWafers)
        {
            if (itemCount == 1)
            {
                var single = items[0];
                var singleResult = new SummaryRows(1);
                singleResult[0] = Create(single, TestItemSummary.CombinedSite, null, single.Values, single.Errors.Count, config, format);
                return singleResult;
            }
            var combinedResults = new SummaryRows(itemCount);
            Parallel.For(0, itemCount, new ParallelOptions { CancellationToken = AnalysisOperation.Token, MaxDegreeOfParallelism = 2 }, i =>
            {
                var item = items[i];
                combinedResults[i] = Create(item, TestItemSummary.CombinedSite, null, item.Values, item.Errors.Count, config, format);
            });
            return combinedResults;
        }

        string?[] waferGroups = combineWafers ? new string?[] { null } : wafers.Select(w => (string?)w).ToArray();
        string[] siteGroups = combineSites ? new[] { TestItemSummary.CombinedSite } : sites as string[] ?? sites.ToArray();
        int groupsPerItem = waferGroups.Length * siteGroups.Length;
        if (groupsPerItem == 0) return new SummaryRows();

        var expandedResults = new SummaryRows(checked(itemCount * groupsPerItem));

        void BuildItem(int itemIndex)
        {
            AnalysisOperation.CheckCancellation();
            var item = items[itemIndex];
            var values = item.Values;
            var groups = new Dictionary<(string? Wafer, string Site), PagedVector<int>>();
            var errors = new Dictionary<(string? Wafer, string Site), int>();
            int i = 0;
            foreach (var sample in values.ReadScopedNumbers())
            {
                if ((i & 1023) == 0) AnalysisOperation.CheckCancellation();
                var key = (combineWafers ? null : sample.Wafer,
                    combineSites ? TestItemSummary.CombinedSite : SiteKey(sample.Site));
                if (!groups.TryGetValue(key, out var indices)) groups[key] = indices = new();
                indices.Add(i++);
            }
            foreach (var error in item.Errors)
            {
                var key = (combineWafers ? null : error.WaferId,
                    combineSites ? TestItemSummary.CombinedSite : SiteKey(error.Site));
                ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(errors, key, out _);
                count++;
            }
            int offset = itemIndex * groupsPerItem;
            foreach (string? wafer in waferGroups)
                foreach (string site in siteGroups)
                {
                    var key = (wafer, site);
                    expandedResults[offset++] = Create(item, site, wafer, new IndexedTestValues(values,
                        groups.TryGetValue(key, out var indices) ? (IReadOnlyList<int>)indices : Array.Empty<int>()),
                        errors.GetValueOrDefault(key), config, format);
                }
        }

        if (itemCount == 1) BuildItem(0);
        else Parallel.For(0, itemCount, new ParallelOptions { CancellationToken = AnalysisOperation.Token, MaxDegreeOfParallelism = 2 }, BuildItem);

        return expandedResults;
    }

    private static string SiteKey(string site) => string.IsNullOrWhiteSpace(site) ? "(空)" : site;

    private static TestItemSummary Create(TestItemData item, string site, string? wafer, IReadOnlyList<TestValue> values, int errors,
        AnalyzerConfig config, string format)
    {
        var cache = item.Values.MetricStore;
        string? cacheKey = cache == null ? null : System.Text.Json.JsonSerializer.Serialize(new { Version = DiskStorage.FormatVersion,
            Scope = item.Values.MetricScope, item.Id, Site = site, Wafer = wafer, item.LowLimit, item.HighLimit, config.RemoveOutliers,
            config.OutlierSigma, item.DisplayFactor, item.DisplayUnit, Format = format, Errors = errors });
        if (cacheKey != null && cache!.ReadMetric(cacheKey) is string cached)
        { var saved = System.Text.Json.JsonSerializer.Deserialize<TestItemSummary>(cached, DiskStorage.JsonOptions)!; saved.Data = item; return saved; }
        using var work = DiskStorage.ReserveWork();
        var (stats, robust, median) = StatisticsService.CalculateSummaryMetrics(
            values, item.LowLimit, item.HighLimit, config.RemoveOutliers, config.OutlierSigma);
        long.TryParse(item.TestNumber, out long testNumber);
        double factor = item.DisplayFactor;
        var result = new TestItemSummary
        {
            Data = item, Site = site, Wafer = wafer ?? TestItemSummary.CombinedWafer,
            IsWaferCombined = wafer == null, TestNumberValue = testNumber,
            LslText = item.DisplayLowLimit?.ToString(format) ?? "-", UslText = item.DisplayHighLimit?.ToString(format) ?? "-",
            Count = stats.Count,
            Fails = stats.Count - stats.InSpec,
            Max = stats.Count > 0 ? stats.Max * factor : null, Min = stats.Count > 0 ? stats.Min * factor : null,
            Eorr = errors,
            SiteSigma = site == TestItemSummary.CombinedSite
                ? StatisticsService.CalculateSiteYieldSigma(values, item.LowLimit, item.HighLimit) : null,
            Mean = stats.Mean * factor, Median = median * factor, Sigma = stats.StdDev * factor,
            RobustMean = robust.Mean * factor, RobustSigma = robust.Sigma * factor, Cpk = stats.Cpk, Cp = stats.Cp, InSpecPercent = stats.InSpecPercent
        };
        if (cacheKey != null) cache!.WriteMetric(cacheKey, System.Text.Json.JsonSerializer.Serialize(result, DiskStorage.JsonOptions));
        return result;
    }
}
