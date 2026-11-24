using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public enum OriginalDisposition { Fail, Pass, PartialPass }

public sealed class HBinClassifier
{
    private readonly HashSet<string> _pass, _partial;
    public HBinClassifier(AnalyzerConfig config) : this(config.PassHBins, config.PartialPassHBins, "HBIN") { }

    private HBinClassifier(IEnumerable<string> pass, IEnumerable<string> partial, string binType)
    {
        _pass = pass.Select(BinStatisticsService.NormalizeBin).Where(bin => bin.Length > 0).ToHashSet(StringComparer.Ordinal);
        _partial = partial.Select(BinStatisticsService.NormalizeBin).Where(bin => bin.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (_pass.Overlaps(_partial)) throw new ArgumentException($"Pass 与 PartialPass {binType} 不允许重叠。");
    }
    // A missing section is different from explicitly configuring an empty pass set.
    internal static HBinClassifier? ForSBin(AnalyzerConfig config) =>
        config.PassSBins == null && config.PartialPassSBins == null ? null :
            new(config.PassSBins ?? Enumerable.Empty<string>(), config.PartialPassSBins ?? Enumerable.Empty<string>(), "SBIN");

    public OriginalDisposition Classify(string bin) => ClassifyNormalized(BinStatisticsService.NormalizeBin(bin));
    internal OriginalDisposition ClassifySBin(string hbin, string sbin, HBinClassifier? softClassifier) =>
        string.IsNullOrWhiteSpace(sbin) ? OriginalDisposition.Fail : softClassifier?.Classify(sbin) ?? Classify(hbin);
    internal OriginalDisposition ClassifyNormalized(string key) => _pass.Contains(key) ? OriginalDisposition.Pass
        : _partial.Contains(key) ? OriginalDisposition.PartialPass : OriginalDisposition.Fail;
    public static bool IsGood(OriginalDisposition disposition, bool includePartial) =>
        disposition == OriginalDisposition.Pass || includePartial && disposition == OriginalDisposition.PartialPass;
}
