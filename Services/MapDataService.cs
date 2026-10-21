using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class MapDataService
{
    public static List<WaferMapPage> PfPages(CsvParseResult data, TestItemData item, string? site)
    {
        bool IncludeSite(string value) => site == null || (string.IsNullOrWhiteSpace(value) ? "(空)" : value) == site;
        // Pool all wafer measurements once. A wafer switch must not change the meaning of a color.
        // Retain the PF map's existing valid-measurement and Site population.
        var scale = HeatmapScale.Create(item.Values.Where(v => IncludeSite(v.Site)).Select(v => v.Value * item.DisplayFactor));
        return data.Wafers.Select(wafer =>
        {
            IEnumerable<CsvRecord> Latest()
            {
                if (data.Records.Store is { } store)
                {
                    var present = store.Query("SELECT slot FROM records WHERE wafer='" + wafer.Replace("'", "''") + "' AND x IS NOT NULL AND y IS NOT NULL ORDER BY slot")
                        .Where(record => IncludeSite(record.Site) && record.Measurements.ContainsKey(item.Id)).ToRecordList();
                    try { foreach (var row in present.Store!.Query("SELECT MAX(slot) FROM records GROUP BY x,y ORDER BY MIN(slot)")) yield return row; }
                    finally { present.Store?.Dispose(); }
                }
                else
                {
                    var latest = new Dictionary<(int X, int Y), CsvRecord>();
                    foreach (var record in data.Records)
                        if (record.WaferId == wafer && IncludeSite(record.Site) && record.X.HasValue && record.Y.HasValue && record.Measurements.ContainsKey(item.Id))
                            latest[(record.X.Value, record.Y.Value)] = record;
                    foreach (var row in latest.Values) yield return row;
                }
            }
            var dies = new PagedObjectRows<WaferDie>(Latest().Select(r =>
            {
                string raw = r.Measurements[item.Id];
                bool valid = DatasetService.TryValue(raw, out double value);
                string category = !valid ? "Eorr" :
                    (item.LowLimit.HasValue && value < item.LowLimit || item.HighLimit.HasValue && value > item.HighLimit) ? "FAIL" : "PASS";
                return new WaferDie
                {
                    WaferId = wafer, X = r.X!.Value, Y = r.Y!.Value, Site = r.Site, SerialNumber = r.SerialNumber,
                    SBin = r.SBin, HBin = r.HBin, Result = r.Result, Unit = item.ShownUnit,
                    Value = valid ? value * item.DisplayFactor : null, RawValue = MeasurementDisplay.ExportText(item, raw), Category = category, Color = WaferMapWindow.ColorForCategory(category)
                };
            }), forceDisk: data.Records.IsOnDisk);
            return new WaferMapPage(wafer, dies, scale, item.ShownUnit, true);
        }).ToList();
    }

    public static List<WaferMapPage> BinPages(CsvParseResult data, bool soft)
    {
        var colors = BinPalette.Create(data.Records.Select(r => soft ? r.SBin : r.HBin));
        return data.Wafers.Select(wafer => new WaferMapPage(wafer,
            new PagedObjectRows<WaferDie>(data.Devices.Where(d => d.WaferId == wafer && d.X.HasValue && d.Y.HasValue).Select(d =>
            {
                string category = BinPalette.Key(soft ? d.SBin : d.HBin);
                return new WaferDie
                {
                    WaferId = wafer, X = d.X!.Value, Y = d.Y!.Value, Site = d.Site, SerialNumber = d.SerialNumber,
                    SBin = d.SBin, HBin = d.HBin, Result = d.Result,
                    Category = category, Color = colors[category]
                };
            }), forceDisk: data.Records.IsOnDisk))).ToList();
    }
}
