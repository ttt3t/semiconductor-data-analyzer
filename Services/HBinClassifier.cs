using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public enum OriginalDisposition { Fail, Pass, PartialPass }

public sealed class HBinClassifier
{
    private readonly HashSet<string> _pass, _partial;
    public HBinClassifier(AnalyzerConfig config)
    {
        _pass = config.PassHBins.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _partial = config.PartialPassHBins.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_pass.Overlaps(_partial)) throw new ArgumentException("Pass 与 PartialPass HBIN 不允许重叠。");
    }
    public OriginalDisposition Classify(string bin)
    {
        string key = BinStatisticsService.NormalizeBin(bin);
        return _pass.Contains(key) ? OriginalDisposition.Pass : _partial.Contains(key)
            ? OriginalDisposition.PartialPass : OriginalDisposition.Fail;
    }
    public static bool IsGood(OriginalDisposition disposition, bool includePartial) =>
        disposition == OriginalDisposition.Pass || includePartial && disposition == OriginalDisposition.PartialPass;
}
