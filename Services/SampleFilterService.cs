using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

// Bounds are stored in source units, so changing display prefixes never changes membership.
public sealed record SampleRange(string ItemId, double? Minimum, double? Maximum);
public enum SampleMatchMode { Intersection, Union }

public static class SampleFilterService
{
    public static CsvParseResult Apply(CsvParseResult data, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection)
    {
        if (rules.Count == 0) return data;
        foreach (var rule in rules)
            if (!data.Items.Any(i => i.Id == rule.ItemId) || rule.Minimum == null && rule.Maximum == null ||
                rule.Minimum is double min && !double.IsFinite(min) || rule.Maximum is double max && !double.IsFinite(max))
                throw new InvalidDataException("筛选项不存在或范围无效，请重新选择测试项并输入范围。");
        var selected = DatasetService.SelectRetestRecords(data.Records, mode);
        bool InRange(CsvRecord record, SampleRange rule) =>
            record.Measurements.TryGetText(rule.ItemId, out var raw) && DatasetService.TryValue(raw, out double value) &&
            (rule.Minimum > rule.Maximum
                ? value >= rule.Minimum!.Value || value <= rule.Maximum!.Value
                : (!rule.Minimum.HasValue || value >= rule.Minimum.Value) && (!rule.Maximum.HasValue || value <= rule.Maximum.Value));
        bool Matches(IEnumerable<bool> flags) => match == SampleMatchMode.Union ? flags.Any(x => x) : flags.All(x => x);
        RecordList records;
        if (mode == RetestMode.All)
        {
            using var ids = new DiskKeySet();
            foreach (var record in selected)
            {
                AnalysisOperation.CheckCancellation();
                if (Matches(rules.Select(rule => InRange(record, rule)))) ids.Add(record.Id);
            }
            records = data.Records.Where(r => ids.Contains(r.Id)).ToRecordList();
        }
        else
        {
            // Partial retests can carry different items in different records of one physical chip.
            using var included = new DiskKeySet();
            using var ordered = selected.IsOnDisk ? selected.Store!.GroupRows() : null;
            if (ordered != null)
            {
                for (int i = 0; i < ordered.Count;)
                {
                    int count = ordered[i].Count; var flags = new bool[rules.Count]; string key = "";
                    for (int j = 0; j < count; j++)
                    {
                        AnalysisOperation.CheckCancellation(); var record = selected[ordered[i++].Slot]; key = RecordList.RecordStore.Key(DatasetService.ChipKey(record));
                        for (int k = 0; k < rules.Count; k++) if (record.Measurements.ContainsKey(rules[k].ItemId)) flags[k] = InRange(record, rules[k]);
                    }
                    if (Matches(flags)) included.Add(key);
                }
            }
            else foreach (var group in selected.GroupBy(DatasetService.ChipKey))
            {
                var flags = new bool[rules.Count];
                foreach (var record in group)
                    for (int i = 0; i < rules.Count; i++) if (record.Measurements.ContainsKey(rules[i].ItemId)) flags[i] = InRange(record, rules[i]);
                if (Matches(flags)) included.Add(RecordList.RecordStore.Key(group.Key));
            }
            records = data.Records.Where(r => included.Contains(RecordList.RecordStore.Key(DatasetService.ChipKey(r)))).ToRecordList();
        }
        AnalysisOperation.CheckCancellation();
        records.ScopeKey = data.Records.ScopeKey + ":filter:" + mode + ":" + match + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rules)));
        var result = DatasetService.CreateView(data, records);
        result.SourceDescription = data.SourceDescription;
        return result;
    }
}
