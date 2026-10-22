using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class DatasetService
{
    /// <summary>Share numeric storage and one row mapping across all selected columns.</summary>
    internal static CsvParseResult CreateView(CsvParseResult source, List<CsvRecord> records)
    {
        var sourceRows = new Dictionary<string, int>(source.Records.Count, StringComparer.Ordinal);
        for (int row = 0; row < source.Records.Count; row++)
        {
            if ((row & 1023) == 0) AnalysisOperation.CheckCancellation();
            sourceRows.Add(source.Records[row].Id, row);
        }
        var rows = new int[records.Count];
        var selected = new Dictionary<string, CsvRecord>(records.Count, StringComparer.Ordinal);
        for (int row = 0; row < records.Count; row++)
        {
            AnalysisOperation.CheckCancellation();
            rows[row] = sourceRows[records[row].Id];
            selected.Add(records[row].Id, records[row]);
        }
        var items = new List<TestItemData>(source.Items.Count);
        foreach (var original in source.Items)
        {
            AnalysisOperation.CheckCancellation();
            var item = CloneItem(original);
            item.Values.InitializeView(original.Values, records, rows, item.Id);
            foreach (var error in original.Errors)
                if (selected.TryGetValue(error.Id, out var record) && record.Measurements.ContainsKey(item.Id))
                    item.Errors.Add(record);
            items.Add(item);
        }
        return new CsvParseResult
        {
            Items = items, Records = records, Devices = BuildDevices(records),
            SourceDescription = source.SourceDescription, StdfMetadata = source.StdfMetadata,
            SerialNumberNotice = source.SerialNumberNotice, ReassignedSerialNumberCount = source.ReassignedSerialNumberCount,
            RetestIdentitySignature = source.RetestIdentitySignature, RetestIdentityNotice = source.RetestIdentityNotice,
            MissingUuidCount = source.MissingUuidCount
        };
    }
}
