using System.Collections;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

internal sealed class DiskDeviceList : IReadOnlyList<DeviceInfo>
{
    private readonly RecordList _records;
    private readonly PagedVector<int> _rows;
    public DiskDeviceList(RecordList records)
    {
        _records = records; records.Flush();
        _rows = records.Store!.Slots("SELECT MAX(slot) FROM records WHERE device IS NOT NULL GROUP BY device ORDER BY MIN(slot)");
    }
    public int Count => _rows.Count;
    public DeviceInfo this[int index]
    {
        get { var r = _records[_rows[index]]; return new() { WaferId = r.WaferId, SerialNumber = r.SerialNumber, X = r.X, Y = r.Y, Site = r.Site, Result = r.Result, SBin = r.SBin, HBin = r.HBin }; }
    }
    public IEnumerator<DeviceInfo> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
