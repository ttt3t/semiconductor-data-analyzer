using System.Globalization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record BinCountRow(string Bin, string Category, int Count, double Percent)
{
    public double BinSort => NumericText.SortKey(Bin);
}
public sealed record BinCategoryRow(string Category, int Count, double Percent);
public sealed record BinSiteRow(string Category, string Bin, double?[] Percentages)
{
    public double BinSort => NumericText.SortKey(Bin);
}
public sealed record BinStatisticsResult(int ChipCount, int MissingSBin, int MissingHBin,
    List<BinCountRow> SBins, List<BinCountRow> HBins, List<BinCategoryRow> Categories)
{
    public int PassCount { get; init; }
    public int PartialPassCount { get; init; }
    public List<string> Sites { get; init; } = new();
    public List<BinSiteRow> SiteRows { get; init; } = new();
}

public static class BinStatisticsService
{
    // Numeric spellings such as 01 and 1.0 can use the same classification rule.
    public static string NormalizeBin(string bin) =>
        decimal.TryParse(bin.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture) : bin.Trim().ToUpperInvariant();

    public static BinStatisticsResult Build(IReadOnlyList<CsvRecord> records, RetestMode mode, AnalyzerConfig config)
    {
        if (mode != RetestMode.First && mode != RetestMode.Last) throw new ArgumentOutOfRangeException(nameof(mode));
        // Bin is a whole-chip result: never mix a partial retest's item values to determine its Bin.
        var chosen = new Dictionary<(string Wafer, int? X, int? Y, string Fallback), CsvRecord>();
        foreach (var record in records)
        {
            var key = DatasetService.ChipKey(record);
            if (mode == RetestMode.First) chosen.TryAdd(key, record);
            else chosen[key] = record;
        }
        var hbinClassifier = new HBinClassifier(config);
        var classification = config.SBinCategories.SelectMany(c => c.Value.Select(b => (Bin: NormalizeBin(b), Category: c.Key)))
            .ToDictionary(p => p.Bin, p => p.Category, StringComparer.OrdinalIgnoreCase);
        var categoryCounts = config.SBinCategories.Keys.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);
        var sbins = new Dictionary<string, int>();
        var hbins = new Dictionary<string, int>();
        static string Site(CsvRecord r) => string.IsNullOrWhiteSpace(r.Site) ? "(空)" : r.Site;
        // Keep columns stable across first/last modes, even when retesting changes Site.
        var sites = records.Select(Site).Distinct().OrderBy(NumericText.SortKey).ThenBy(s => s, StringComparer.Ordinal).ToList();
        var siteTotals = new Dictionary<string, int>();
        var siteBins = new Dictionary<(string Site, string Bin), int>();
        foreach (var record in chosen.Values)
        {
            if (!string.IsNullOrWhiteSpace(record.SBin))
            {
                sbins[record.SBin] = sbins.GetValueOrDefault(record.SBin) + 1;
                string site = Site(record);
                siteTotals[site] = siteTotals.GetValueOrDefault(site) + 1;
                var key = (site, record.SBin); siteBins[key] = siteBins.GetValueOrDefault(key) + 1;
            }
            if (!string.IsNullOrWhiteSpace(record.HBin)) hbins[record.HBin] = hbins.GetValueOrDefault(record.HBin) + 1;
        }
        int sTotal = sbins.Values.Sum(), hTotal = hbins.Values.Sum();
        string Category(string bin) => classification.GetValueOrDefault(NormalizeBin(bin), "未分类");
        foreach (var bin in sbins)
        {
            string category = Category(bin.Key);
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + bin.Value;
        }
        var order = categoryCounts.Keys.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        double Percent(int count, int total) => total == 0 ? 0 : count * 100.0 / total;
        var sRows = sbins.Select(p => new BinCountRow(p.Key, Category(p.Key), p.Value, Percent(p.Value, sTotal)))
            .OrderBy(r => order[r.Category]).ThenBy(r => r.BinSort).ThenBy(r => r.Bin, StringComparer.OrdinalIgnoreCase).ToList();
        var hRows = hbins.Select(p => new BinCountRow(p.Key, "", p.Value, Percent(p.Value, hTotal)))
            .OrderBy(r => r.BinSort).ThenBy(r => r.Bin, StringComparer.OrdinalIgnoreCase).ToList();
        return new(chosen.Count, chosen.Count - sTotal, chosen.Count - hTotal, sRows, hRows,
            categoryCounts.Select(p => new BinCategoryRow(p.Key, p.Value, Percent(p.Value, sTotal))).ToList())
        {
            Sites = sites,
            PassCount = chosen.Values.Count(r => hbinClassifier.Classify(r.HBin) == OriginalDisposition.Pass),
            PartialPassCount = chosen.Values.Count(r => hbinClassifier.Classify(r.HBin) == OriginalDisposition.PartialPass),
            SiteRows = sRows.Select(r => new BinSiteRow(r.Category, r.Bin, sites.Select(s =>
                siteTotals.GetValueOrDefault(s) == 0 ? (double?)null : Percent(siteBins.GetValueOrDefault((s, r.Bin)), siteTotals[s])).ToArray())).ToList()
        };
    }
}
