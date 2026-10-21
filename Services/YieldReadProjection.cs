using System.Globalization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>
/// Shared parser sink for product statistics. Keep identities and selected UUID
/// text only; do not build numeric columns or a full raw-measurement snapshot.
/// </summary>
internal sealed class YieldReadProjection
{
    private readonly List<TestItemData> _items;
    private readonly int[] _columns;

    internal YieldReadProjection(List<TestItemData> items, AnalyzerConfig config)
    {
        _items = items;
        if (config.TestMode != TestAnalysisMode.FT) { _columns = Array.Empty<int>(); return; }
        // Apply exactly the analyzer's ambiguous/missing UUID definition rules.
        _ = new RetestIdentityService.Reader(items, config);
        var numbers = RetestIdentityService.ParseTestNumbers(string.Join(",", config.UuidTestNumbers))
            .ToHashSet(StringComparer.Ordinal);
        _columns = Enumerable.Range(0, items.Count).Where(i =>
            ulong.TryParse(items[i].TestNumber.Trim(), out ulong number) &&
            numbers.Contains(number.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    internal void Capture(CsvRecord record, IReadOnlyList<string?> values)
    {
        for (int i = 0; i < values.Count; i++)
            if (!string.IsNullOrWhiteSpace(values[i])) { record.HasNonblankMeasurements = true; break; }
        foreach (int column in _columns)
            if (values[column] is { } value) record.Measurements.Add(_items[column].Id, value);
    }

    internal void CaptureStdf(CsvRecord record, byte[] states, double[] numbers, Dictionary<int, string>? invalid)
    {
        record.HasNonblankMeasurements = states.Any(state =>
            state is RawMeasurementStore.StateNumeric or RawMeasurementStore.StateNotApplicable or RawMeasurementStore.StateText);
        foreach (int column in _columns)
        {
            if (states[column] == RawMeasurementStore.StateAbsent) continue;
            string value = states[column] switch
            {
                RawMeasurementStore.StateNumeric => numbers[column].ToString("R", CultureInfo.InvariantCulture),
                RawMeasurementStore.StateNotApplicable => StdfParser.NotApplicableValue,
                RawMeasurementStore.StateText => invalid![column],
                _ => ""
            };
            record.Measurements.Add(_items[column].Id, value);
        }
    }

    internal CsvParseResult Complete(RecordList records) =>
        new() { Items = _items, Records = records, IsYieldProjection = true };

    internal static CsvParseResult PrepareIdentity(CsvParseResult data, AnalyzerConfig config)
    {
        RetestIdentityService.Reader? reader = config.TestMode == TestAnalysisMode.FT ? new(data.Items, config) : null;
        foreach (var record in data.Records)
        {
            AnalysisOperation.CheckCancellation();
            record.IsFtIdentity = reader != null;
            if (reader != null) record.RetestUuid = reader.Read(record);
            record.UuidTestNumbers = reader?.Recipe ?? record.UuidTestNumbers;
            if (reader != null && record.RetestUuid == "") data.MissingUuidCount++;
        }
        return data;
    }
}
