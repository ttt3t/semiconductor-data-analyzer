using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class RetestPreference
{
    public static bool Replace(bool previousGood, bool nextGood, bool preferGood, RetestMode mode) =>
        preferGood ? nextGood || !previousGood : mode != RetestMode.First;

    public static List<CsvRecord> SelectWhole(IReadOnlyList<CsvRecord> records, RetestMode mode, AnalyzerConfig config)
    {
        if (mode is not (RetestMode.First or RetestMode.Last)) throw new ArgumentOutOfRangeException(nameof(mode));
        bool prefer = config.PreferGoodRetest && mode == RetestMode.Last;
        var classifier = prefer ? new HBinClassifier(config) : null;
        bool Good(CsvRecord r) => classifier != null && HBinClassifier.IsGood(classifier.Classify(r.HBin), config.GoodRetestIncludePartialPass);
        var chosen = new Dictionary<(string, int?, int?, string), CsvRecord>();
        foreach (var row in records)
        {
            AnalysisOperation.CheckCancellation();
            var key = DatasetService.ChipKey(row);
            if (!chosen.TryGetValue(key, out var previous) || Replace(Good(previous), Good(row), prefer, mode)) chosen[key] = row;
        }
        // Preserve acquisition order and original identities; no raw measurements are copied or rewritten.
        var selected = new List<CsvRecord>(chosen.Count);
        foreach (var row in records)
        {
            AnalysisOperation.CheckCancellation();
            if (ReferenceEquals(chosen[DatasetService.ChipKey(row)], row)) selected.Add(row);
        }
        return selected;
    }

    public static string Signature(AnalyzerConfig config) => config.PreferGoodRetest
        ? $"True|{config.GoodRetestIncludePartialPass}|{string.Join(',', config.PassHBins.Order(StringComparer.Ordinal))}|{string.Join(',', config.PartialPassHBins.Order(StringComparer.Ordinal))}"
        : "False";
}
