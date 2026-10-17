using System.Runtime.InteropServices;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class SummaryService
{
    public static List<TestItemSummary> Build(IReadOnlyList<TestItemData> items, IReadOnlyList<string> sites,
        bool combineSites, AnalyzerConfig config)
        => Build(items, sites, Array.Empty<string>(), combineSites, true, config);

    public static List<TestItemSummary> Build(IReadOnlyList<TestItemData> items, IReadOnlyList<string> sites,
        IReadOnlyList<string> wafers, bool combineSites, bool combineWafers, AnalyzerConfig config)
    {
        AnalysisOperation.CheckCancellation();
        int itemCount = items.Count;
        if (itemCount == 0) return new List<TestItemSummary>();
        string format = $"F{config.DecimalPlaces}";

        if (combineSites && combineWafers)
        {
            if (itemCount == 1)
            {
                var single = items[0];
                return new List<TestItemSummary>(1)
                {
                    Create(single, TestItemSummary.CombinedSite, null, single.Values, single.Errors.Count, config, format)
                };
            }
            var combinedResults = new TestItemSummary[itemCount];
            Parallel.For(0, itemCount, new ParallelOptions { CancellationToken = AnalysisOperation.Token }, i =>
            {
                var item = items[i];
                combinedResults[i] = Create(item, TestItemSummary.CombinedSite, null, item.Values, item.Errors.Count, config, format);
            });
            return new List<TestItemSummary>(combinedResults);
        }

        string?[] waferGroups = combineWafers ? new string?[] { null } : wafers.Select(w => (string?)w).ToArray();
        string[] siteGroups = combineSites ? new[] { TestItemSummary.CombinedSite } : sites as string[] ?? sites.ToArray();
        int groupsPerItem = waferGroups.Length * siteGroups.Length;
        if (groupsPerItem == 0) return new List<TestItemSummary>();

        var expandedResults = new TestItemSummary[itemCount * groupsPerItem];

        void BuildItem(int itemIndex)
        {
            AnalysisOperation.CheckCancellation();
            var item = items[itemIndex];
            var values = item.Values;
            var groups = new Dictionary<(string? Wafer, string Site), List<int>>();
            var errors = new Dictionary<(string? Wafer, string Site), int>();
            for (int i = 0; i < values.Count; i++)
            {
                if ((i & 1023) == 0) AnalysisOperation.CheckCancellation();
                var record = values.GetRecordAt(i);
                var key = (combineWafers ? null : record.WaferId,
                    combineSites ? TestItemSummary.CombinedSite : SiteKey(record.Site));
                if (!groups.TryGetValue(key, out var indices)) groups[key] = indices = new();
                indices.Add(i);
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
                        groups.TryGetValue(key, out var indices) ? indices : Array.Empty<int>()),
                        errors.GetValueOrDefault(key), config, format);
                }
        }

        if (itemCount == 1) BuildItem(0);
        else Parallel.For(0, itemCount, new ParallelOptions { CancellationToken = AnalysisOperation.Token }, BuildItem);

        return new List<TestItemSummary>(expandedResults);
    }

    private static string SiteKey(string site) => string.IsNullOrWhiteSpace(site) ? "(空)" : site;

    private static TestItemSummary Create(TestItemData item, string site, string? wafer, IReadOnlyList<TestValue> values, int errors,
        AnalyzerConfig config, string format)
    {
        var (stats, robust, median) = StatisticsService.CalculateSummaryMetrics(
            values, item.LowLimit, item.HighLimit, config.RemoveOutliers, config.OutlierSigma);
        long.TryParse(item.TestNumber, out long testNumber);
        return new TestItemSummary
        {
            Data = item, Site = site, Wafer = wafer ?? TestItemSummary.CombinedWafer,
            IsWaferCombined = wafer == null, TestNumberValue = testNumber,
            LslText = item.LowLimit?.ToString(format) ?? "-", UslText = item.HighLimit?.ToString(format) ?? "-",
            Count = stats.Count,
            Fails = stats.Count - stats.InSpec,
            Max = stats.Count > 0 ? stats.Max : null, Min = stats.Count > 0 ? stats.Min : null,
            Eorr = errors,
            SiteSigma = site == TestItemSummary.CombinedSite
                ? StatisticsService.CalculateSiteYieldSigma(values, item.LowLimit, item.HighLimit) : null,
            Mean = stats.Mean, Median = median, Sigma = stats.StdDev,
            RobustMean = robust.Mean, RobustSigma = robust.Sigma, Cpk = stats.Cpk, Cp = stats.Cp, InSpecPercent = stats.InSpecPercent
        };
    }
}
