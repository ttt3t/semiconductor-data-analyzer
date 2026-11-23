using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

// Bounds are stored in source units, so changing display prefixes never changes membership.
public sealed record SampleRange(string ItemId, double? Minimum, double? Maximum);
public enum SampleMatchMode { Intersection, Union }

public static class SampleFilterService
{
    public static CsvParseResult Apply(CsvParseResult data, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection, AnalyzerConfig? config = null, IReadOnlyList<string>? sBins = null)
    {
        if (rules.Count == 0 && sBins is not { Count: > 0 }) return data;
        AnalysisOperation.CheckCancellation();
        foreach (var rule in rules)
            if (!data.Items.Any(i => i.Id == rule.ItemId) || rule.Minimum == null && rule.Maximum == null ||
                rule.Minimum is double min && !double.IsFinite(min) || rule.Maximum is double max && !double.IsFinite(max))
                throw new InvalidDataException("筛选项不存在或范围无效，请重新选择测试项并输入范围。");
        var eligible = data.Records;
        if (sBins is { Count: > 0 })
        {
            var allowed = sBins.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.Ordinal);
            var byRawBin = new Dictionary<string, bool>(StringComparer.Ordinal);
            eligible = new List<CsvRecord>();
            foreach (var record in data.Records)
            {
                AnalysisOperation.CheckCancellation();
                if (!byRawBin.TryGetValue(record.SBin, out bool keep))
                    byRawBin[record.SBin] = keep = allowed.Contains(BinStatisticsService.NormalizeBin(record.SBin));
                if (keep) eligible.Add(record);
            }
        }
        if (rules.Count == 0)
            return eligible.Count == data.Records.Count ? data : DatasetService.CreateView(data, eligible);

        var selected = DatasetService.SelectRetestRecords(eligible, mode, config);
        bool InRange(CsvRecord record, SampleRange rule) =>
            record.Measurements.TryGetText(rule.ItemId, out var raw) && DatasetService.TryValue(raw, out double value) &&
            (rule.Minimum > rule.Maximum
                ? value >= rule.Minimum!.Value || value <= rule.Maximum!.Value
                : (!rule.Minimum.HasValue || value >= rule.Minimum.Value) && (!rule.Maximum.HasValue || value <= rule.Maximum.Value));
        bool Matches(IEnumerable<bool> flags) => match == SampleMatchMode.Union ? flags.Any(x => x) : flags.All(x => x);
        List<CsvRecord> records;
        if (mode == RetestMode.All)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in selected)
            {
                AnalysisOperation.CheckCancellation();
                if (Matches(rules.Select(rule => InRange(record, rule)))) ids.Add(record.Id);
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
                    if (record.Measurements.ContainsKey(rules[i].ItemId)) flags[i] = InRange(record, rules[i]);
            }
            var included = matches.Where(p => Matches(p.Value)).Select(p => p.Key).ToHashSet();
            records = eligible.Where(r => included.Contains(DatasetService.ChipKey(r))).ToList();
        }
        AnalysisOperation.CheckCancellation();
        var result = DatasetService.CreateView(data, records);
        result.SourceDescription = data.SourceDescription;
        return result;
    }
}
