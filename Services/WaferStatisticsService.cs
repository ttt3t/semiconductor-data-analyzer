using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record WaferBinRow(string Bin, string Category, int[] Counts, double?[] Percentages);
public sealed record WaferStatisticsResult(List<string> Wafers, int[] ChipCounts, int[] ValidSBinCounts,
    List<WaferBinRow> Rows)
{
    public int[] ValidHBinCounts { get; init; } = Array.Empty<int>();
    public List<WaferBinRow> HBinRows { get; init; } = new();
    public List<WaferBinRow> CategoryRows { get; init; } = new();
}

public static class WaferStatisticsService
{
    public static WaferStatisticsResult Build(IReadOnlyList<CsvRecord> records, RetestMode mode, AnalyzerConfig config)
    {
        if (mode is not (RetestMode.First or RetestMode.Last)) throw new ArgumentOutOfRangeException(nameof(mode));
        // Reuse BIN Table's whole-chip retest selection and category definitions.
        // Only compact counts survive this method; no measurement columns are copied.
        var groups = records.GroupBy(r => r.WaferId, StringComparer.Ordinal).ToArray();
        var summaries = groups.Select(g => BinStatisticsService.Build(g.ToArray(), mode, config)).ToArray();
        var valid = summaries.Select(s => s.ChipCount - s.MissingSBin).ToArray();
        var lookup = summaries.Select(s => s.SBins.ToDictionary(r => r.Bin, r => r.Count, StringComparer.Ordinal)).ToArray();
        var categoryOrder = config.SBinCategories.Keys.Select((name, index) => (name, index))
            .ToDictionary(p => p.name, p => p.index, StringComparer.OrdinalIgnoreCase);
        var rows = summaries.SelectMany(s => s.SBins).DistinctBy(r => r.Bin, StringComparer.Ordinal)
            .OrderBy(r => categoryOrder.GetValueOrDefault(r.Category, int.MaxValue))
            .ThenBy(r => r.BinSort).ThenBy(r => r.Bin, StringComparer.Ordinal)
            .Select(r => new WaferBinRow(r.Bin, r.Category,
                lookup.Select(bins => bins.GetValueOrDefault(r.Bin)).ToArray(),
                lookup.Select((bins, i) => valid[i] == 0 ? (double?)null : 100.0 * bins.GetValueOrDefault(r.Bin) / valid[i]).ToArray()))
            .ToList();
        var hValid = summaries.Select(s => s.ChipCount - s.MissingHBin).ToArray();
        var hLookup = summaries.Select(s => s.HBins.ToDictionary(r => r.Bin, r => r.Count, StringComparer.Ordinal)).ToArray();
        var hRows = summaries.SelectMany(s => s.HBins).DistinctBy(r => r.Bin, StringComparer.Ordinal)
            .OrderBy(r => r.BinSort).ThenBy(r => r.Bin, StringComparer.Ordinal)
            .Select(r => new WaferBinRow(r.Bin, "",
                hLookup.Select(bins => bins.GetValueOrDefault(r.Bin)).ToArray(),
                hLookup.Select((bins, i) => hValid[i] == 0 ? (double?)null : 100.0 * bins.GetValueOrDefault(r.Bin) / hValid[i]).ToArray()))
            .ToList();
        var categoryLookup = summaries.Select(s => s.Categories.ToDictionary(r => r.Category, r => r.Count, StringComparer.OrdinalIgnoreCase)).ToArray();
        var categoryRows = summaries.SelectMany(s => s.Categories).DistinctBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => categoryOrder.GetValueOrDefault(r.Category, int.MaxValue))
            .Select(r => new WaferBinRow("", r.Category,
                categoryLookup.Select(categories => categories.GetValueOrDefault(r.Category)).ToArray(),
                categoryLookup.Select((categories, i) => valid[i] == 0 ? (double?)null : 100.0 * categories.GetValueOrDefault(r.Category) / valid[i]).ToArray()))
            .ToList();
        return new(groups.Select(g => g.Key).ToList(), summaries.Select(s => s.ChipCount).ToArray(), valid, rows)
        { ValidHBinCounts = hValid, HBinRows = hRows, CategoryRows = categoryRows };
    }
}
