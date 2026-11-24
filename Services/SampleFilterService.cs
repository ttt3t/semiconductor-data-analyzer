using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

// Bounds are stored in source units, so changing display prefixes never changes membership.
public sealed record SampleRange(string ItemId, double? Minimum, double? Maximum)
{
    public bool Outside { get; init; }
    public SampleDeviation? Deviation { get; init; }
}
public enum SampleMatchMode { Intersection, Union }
public enum SampleCalculation { Bounds, Relative, Absolute }
public enum SampleBaseline { Custom, Mean, Median, SpecCenter, Golden, WaferMean }
public sealed record SampleDeviation(SampleCalculation Calculation, SampleBaseline Baseline,
    double? Negative, double? Positive, double? Reference = null, string? GoldenLabel = null);
public readonly record struct SampleLocation(string Lot, string? Wafer = null)
{
    internal static SampleLocation FromRecord(CsvRecord record) => new(record.Lot.Trim(), record.WaferId);
}

public static partial class SampleFilterService
{
    public static CsvParseResult Apply(CsvParseResult data, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection, AnalyzerConfig? config = null, IReadOnlyList<string>? sBins = null,
        IReadOnlyList<SampleLocation>? locations = null)
    {
        if (rules.Count == 0 && sBins is not { Count: > 0 } && locations is not { Count: > 0 }) return data;
        AnalysisOperation.CheckCancellation();
        var matchesRange = rules.Count > 0 ? RangeMatcher(data, rules, mode, match, config) : null;
        var allowedLocations = locations?.ToHashSet();
        var allowedBins = sBins?.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.Ordinal);
        var byRawBin = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool MatchesBin(string bin)
        {
            if (allowedBins is not { Count: > 0 }) return false;
            if (!byRawBin.TryGetValue(bin, out bool keep))
                byRawBin[bin] = keep = allowedBins.Contains(BinStatisticsService.NormalizeBin(bin));
            return keep;
        }
        bool MatchesLocation(CsvRecord record)
        {
            if (allowedLocations is not { Count: > 0 }) return false;
            var location = SampleLocation.FromRecord(record);
            return allowedLocations.Contains(location) || allowedLocations.Contains(new(location.Lot));
        }
        var records = new List<CsvRecord>();
        foreach (var record in data.Records)
        {
            AnalysisOperation.CheckCancellation();
            if (MatchesBin(record.SBin) || MatchesLocation(record) || matchesRange?.Invoke(record) == true)
                records.Add(record);
        }
        AnalysisOperation.CheckCancellation();
        if (records.Count == data.Records.Count) return data;
        var result = DatasetService.CreateView(data, records);
        result.SourceDescription = data.SourceDescription;
        return result;
    }

    private static Func<CsvRecord, bool> RangeMatcher(CsvParseResult data, IReadOnlyList<SampleRange> rules,
        RetestMode mode, SampleMatchMode match, AnalyzerConfig? config)
    {
        var selected = DatasetService.SelectRetestRecords(data.Records, mode, config);
        var items = data.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var resolved = rules.Select(rule => Resolve(items.GetValueOrDefault(rule.ItemId), rule, selected)).ToArray();
        bool InRange(CsvRecord record, int index) =>
            resolved[index].ForRecord(record) is { } interval && ReadValue(record, rules[index].ItemId) is double value &&
            interval.Matches(value, rules[index].Outside);
        bool Matches(IEnumerable<bool> flags) => match == SampleMatchMode.Union ? flags.Any(x => x) : flags.All(x => x);
        var indices = Enumerable.Range(0, rules.Count);
        if (mode == RetestMode.All)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in selected)
            {
                AnalysisOperation.CheckCancellation();
                if (Matches(indices.Select(index => InRange(record, index)))) ids.Add(record.Id);
            }
            return record => ids.Contains(record.Id);
        }
        // Partial retests can carry different items in different records of one physical chip.
        var matches = new Dictionary<(string Wafer, int? X, int? Y, string Fallback), bool[]>();
        foreach (var record in selected)
        {
            AnalysisOperation.CheckCancellation();
            var key = DatasetService.ChipKey(record);
            if (!matches.TryGetValue(key, out var flags)) matches[key] = flags = new bool[rules.Count];
            for (int i = 0; i < rules.Count; i++)
                if (record.Measurements.ContainsKey(rules[i].ItemId)) flags[i] = InRange(record, i);
        }
        var included = matches.Where(p => Matches(p.Value)).Select(p => p.Key).ToHashSet();
        return record => included.Contains(DatasetService.ChipKey(record));
    }
}
