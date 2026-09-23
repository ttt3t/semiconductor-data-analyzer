using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class SummaryService
{
    public static List<TestItemSummary> Build(IReadOnlyList<TestItemData> items, IReadOnlyList<string> sites,
        bool combineSites, AnalyzerConfig config)
    {
        var summaries = new List<TestItemSummary>();
        string format = $"F{config.DecimalPlaces}";
        foreach (var item in items)
        {
            if (combineSites) summaries.Add(Create(item, TestItemSummary.CombinedSite, item.Values, config, format));
            else
            {
                var groups = new Dictionary<string, List<int>>();
                for (int i = 0; i < item.Values.Count; i++)
                {
                    string site = SiteKey(item.Values[i].Site);
                    if (!groups.TryGetValue(site, out var indices)) groups[site] = indices = new();
                    indices.Add(i);
                }
                foreach (string site in sites)
                    summaries.Add(Create(item, site, new IndexedTestValues(item.Values,
                        groups.TryGetValue(site, out var indices) ? indices : Array.Empty<int>()), config, format));
            }
        }
        return summaries;
    }

    private static string SiteKey(string site) => string.IsNullOrWhiteSpace(site) ? "(空)" : site;

    private static TestItemSummary Create(TestItemData item, string site, IReadOnlyList<TestValue> values,
        AnalyzerConfig config, string format)
    {
        var stats = StatisticsService.Calculate(values, item.LowLimit, item.HighLimit);
        var robust = StatisticsService.CalculateRobust(values, stats, config.RemoveOutliers, config.OutlierSigma);
        long.TryParse(item.TestNumber, out long testNumber);
        return new TestItemSummary
        {
            Data = item, Site = site, TestNumberValue = testNumber,
            LslText = item.LowLimit?.ToString(format) ?? "-", UslText = item.HighLimit?.ToString(format) ?? "-",
            Count = stats.Count,
            Eorr = site == TestItemSummary.CombinedSite ? item.Errors.Count : item.Errors.Count(r => SiteKey(r.Site) == site),
            SiteSigma = site == TestItemSummary.CombinedSite ? StatisticsService.CalculateSiteYieldSigma(item) : null,
            Mean = stats.Mean, Median = StatisticsService.CalculateMedian(values), Sigma = stats.StdDev,
            RobustMean = robust.Mean, RobustSigma = robust.Sigma, Cpk = stats.Cpk, Cp = stats.Cp, InSpecPercent = stats.InSpecPercent
        };
    }
}
