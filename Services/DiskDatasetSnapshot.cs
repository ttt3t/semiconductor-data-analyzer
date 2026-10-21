using System.IO;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Small manifest referencing immutable local files; measurements never travel through JSON/IPC.</summary>
internal sealed class DiskDatasetSnapshot : IDisposable
{
    public int Version { get; set; } = DiskStorage.FormatVersion;
    public required RecordList.Snapshot Records { get; set; }
    public required TestItemData[] Items { get; set; }
    public required TestValueColumn.Snapshot[] Columns { get; set; }
    public required PagedVector<int>.Snapshot[] Errors { get; set; }
    public string SourceDescription { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] private readonly List<IDisposable> _temporary = new();
    internal static DiskDatasetSnapshot Capture(CsvParseResult data)
    {
        var result = new DiskDatasetSnapshot { Records = data.Records.Capture(), Items = data.Items.ToArray(), Columns = new TestValueColumn.Snapshot[data.Items.Count], Errors = new PagedVector<int>.Snapshot[data.Items.Count], SourceDescription = data.SourceDescription };
        var maps = new Dictionary<object, PagedVector<int>.Snapshot>();
        try
        {
            for (int i = 0; i < data.Items.Count; i++)
            {
                result.Columns[i] = data.Items[i].Values.Capture(maps, result._temporary);
                var errors = new PagedVector<int>(); result._temporary.Add(errors);
                foreach (var error in data.Items[i].Errors)
                { AnalysisOperation.CheckCancellation(); int row = data.Records.FindId(error.Id); if (row >= 0) errors.Add(row); }
                result.Errors[i] = errors.Capture();
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal CsvParseResult Open()
    {
        if (Version != DiskStorage.FormatVersion || Items.Length != Columns.Length || Errors.Length != Items.Length) throw new InvalidDataException("导出缓存版本不匹配。");
        var records = RecordList.Open(Records);
        // Child input is immutable; it is never committed back into the main process.
        var stores = new Dictionary<string, (DiskColumnStore Store, Dictionary<int, int> Indices)>(StringComparer.Ordinal);
        foreach (var group in Columns.Select((c, i) => (c, i)).GroupBy(p => p.c.Data.Path))
        {
            var entries = group.ToArray(); var store = new DiskColumnStore(entries.Select(p => p.c.Data).ToArray());
            stores.Add(group.Key, (store, entries.Select((p, i) => (p.i, i)).ToDictionary(p => p.Item1, p => p.Item2)));
        }
        for (int i = 0; i < Items.Length; i++)
        {
            var store = stores[Columns[i].Data.Path]; Items[i].Values.Open(Columns[i], records, Items[i].Id, store.Store, store.Indices[i]);
            using var errors = PagedVector<int>.Open(Errors[i]);
            foreach (int row in errors) Items[i].Errors.Add(records[row]);
        }
        return new CsvParseResult { Records = records, Items = Items.ToList(), Devices = DatasetService.BuildDevices(records), SourceDescription = SourceDescription };
    }
    public void Dispose() { foreach (var resource in _temporary) resource.Dispose(); _temporary.Clear(); }
}
