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

public static partial class SampleFilterService
{
    public static CsvParseResult Apply(CsvParseResult data, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection, AnalyzerConfig? config = null, IReadOnlyList<string>? sBins = null)
    {
        if (rules.Count == 0 && sBins is not { Count: > 0 }) return data;
        AnalysisOperation.CheckCancellation();
        var eligible = EligibleRecords(data, sBins);
        if (rules.Count == 0)
            return eligible.Count == data.Records.Count ? data : DatasetService.CreateView(data, eligible);

        var selected = DatasetService.SelectRetestRecords(eligible, mode, config);
        var items = data.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var resolved = rules.Select(rule => Resolve(items.GetValueOrDefault(rule.ItemId), rule, selected)).ToArray();
        bool InRange(CsvRecord record, int index) =>
            resolved[index].ForRecord(record) is { } interval && ReadValue(record, rules[index].ItemId) is double value &&
            interval.Matches(value, rules[index].Outside);
        bool Matches(IEnumerable<bool> flags) => match == SampleMatchMode.Union ? flags.Any(x => x) : flags.All(x => x);
        var indices = Enumerable.Range(0, rules.Count);
        List<CsvRecord> records;
        if (mode == RetestMode.All)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in selected)
            {
                AnalysisOperation.CheckCancellation();
                if (Matches(indices.Select(index => InRange(record, index)))) ids.Add(record.Id);
            }
            records = eligible.Where(r => ids.Contains(r.Id)).ToList();
        }
        else
        {
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
            records = eligible.Where(r => included.Contains(DatasetService.ChipKey(r))).ToList();
        }
        AnalysisOperation.CheckCancellation();
        var result = DatasetService.CreateView(data, records);
        result.SourceDescription = data.SourceDescription;
        return result;
    }

    private static List<CsvRecord> EligibleRecords(CsvParseResult data, IReadOnlyList<string>? sBins)
    {
        if (sBins is not { Count: > 0 }) return data.Records;
        var allowed = sBins.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.Ordinal);
        var byRawBin = new Dictionary<string, bool>(StringComparer.Ordinal);
        var eligible = new List<CsvRecord>();
        foreach (var record in data.Records)
        {
            AnalysisOperation.CheckCancellation();
            if (!byRawBin.TryGetValue(record.SBin, out bool keep))
                byRawBin[record.SBin] = keep = allowed.Contains(BinStatisticsService.NormalizeBin(record.SBin));
            if (keep) eligible.Add(record);
        }
        return eligible;
    }
}
