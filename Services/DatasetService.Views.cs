using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class DatasetService
{
    /// <summary>Share numeric storage and one row mapping across all selected columns.</summary>
    internal static CsvParseResult CreateView(CsvParseResult source, RecordList records)
    {
        records.Inherit(source.Records);
        Dictionary<string, int>? sourceRows = null;
        if (!source.Records.IsOnDisk)
        {
            sourceRows = new(source.Records.Count, StringComparer.Ordinal);
            for (int i = 0; i < source.Records.Count; i++) sourceRows.Add(source.Records[i].Id, i);
        }
        var rows = new PagedVector<int>();
        foreach (var record in records)
        {
            AnalysisOperation.CheckCancellation();
            rows.Add(sourceRows == null ? source.Records.FindId(record.Id) : sourceRows[record.Id]);
        }
        rows.Seal();
        var items = new List<TestItemData>(source.Items.Count);
        foreach (var original in source.Items)
        {
            AnalysisOperation.CheckCancellation();
            var item = CloneItem(original);
            item.Values.InitializeView(original.Values, records, rows, item.Id);
            foreach (var error in original.Errors)
                if (records.FindId(error.Id) is int row && row >= 0 && records[row].Measurements.ContainsKey(item.Id))
                    item.Errors.Add(records[row]);
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
