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
        var summaries = new List<TestItemSummary>();
        string format = $"F{config.DecimalPlaces}";
        foreach (var item in items)
        {
            if (combineSites && combineWafers)
                summaries.Add(Create(item, TestItemSummary.CombinedSite, null, item.Values, item.Errors.Count, config, format));
            else
            {
                // Bucket row indexes once per item, rather than scanning the entire column for every cell group.
                var groups = new Dictionary<(string? Wafer, string Site), List<int>>();
                var errors = new Dictionary<(string? Wafer, string Site), int>();
                for (int i = 0; i < item.Values.Count; i++)
                {
                    var value = item.Values[i];
                    var key = (combineWafers ? null : value.WaferId,
                        combineSites ? TestItemSummary.CombinedSite : SiteKey(value.Site));
                    if (!groups.TryGetValue(key, out var indices)) groups[key] = indices = new();
                    indices.Add(i);
                }
                foreach (var error in item.Errors)
                {
                    var key = (combineWafers ? null : error.WaferId,
                        combineSites ? TestItemSummary.CombinedSite : SiteKey(error.Site));
                    errors.TryGetValue(key, out int count);
                    errors[key] = count + 1;
                }
                IEnumerable<string?> waferGroups = combineWafers ? (IEnumerable<string?>)new string?[] { null } : wafers;
                var siteGroups = combineSites ? new[] { TestItemSummary.CombinedSite } : sites;
                foreach (string? wafer in waferGroups)
                foreach (string site in siteGroups)
                {
                    var key = (wafer, site);
                    summaries.Add(Create(item, site, wafer, new IndexedTestValues(item.Values,
                        groups.TryGetValue(key, out var indices) ? indices : Array.Empty<int>()),
                        errors.GetValueOrDefault(key), config, format));
                }
            }
        }
        return summaries;
    }

    private static string SiteKey(string site) => string.IsNullOrWhiteSpace(site) ? "(空)" : site;

    private static TestItemSummary Create(TestItemData item, string site, string? wafer, IReadOnlyList<TestValue> values, int errors,
        AnalyzerConfig config, string format)
    {
        var stats = StatisticsService.Calculate(values, item.LowLimit, item.HighLimit);
        var robust = StatisticsService.CalculateRobust(values, stats, config.RemoveOutliers, config.OutlierSigma);
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
            Mean = stats.Mean, Median = StatisticsService.CalculateMedian(values), Sigma = stats.StdDev,
            RobustMean = robust.Mean, RobustSigma = robust.Sigma, Cpk = stats.Cpk, Cp = stats.Cp, InSpecPercent = stats.InSpecPercent
        };
    }
}
