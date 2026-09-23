using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class MapDataService
{
    public static List<WaferMapPage> PfPages(CsvParseResult data, TestItemData item, string? site)
    {
        bool IncludeSite(string value) => site == null || (string.IsNullOrWhiteSpace(value) ? "(空)" : value) == site;
        // Pool all wafer measurements once. A wafer switch must not change the meaning of a color.
        // Retain the PF map's existing valid-measurement and Site population.
        var scale = HeatmapScale.Create(item.Values.Where(v => IncludeSite(v.Site)).Select(v => v.Value));
        return data.Wafers.Select(wafer =>
        {
            var latest = new Dictionary<(int X, int Y), CsvRecord>();
            foreach (var record in data.Records)
                if (record.WaferId == wafer && IncludeSite(record.Site) && record.X.HasValue && record.Y.HasValue &&
                    record.Measurements.ContainsKey(item.Id)) latest[(record.X.Value, record.Y.Value)] = record;
            var dies = latest.Values.Select(r =>
            {
                string raw = r.Measurements[item.Id];
                bool valid = DatasetService.TryValue(raw, out double value);
                string category = !valid ? "Eorr" :
                    (item.LowLimit.HasValue && value < item.LowLimit || item.HighLimit.HasValue && value > item.HighLimit) ? "FAIL" : "PASS";
                return new WaferDie
                {
                    WaferId = wafer, X = r.X!.Value, Y = r.Y!.Value, Site = r.Site, SerialNumber = r.SerialNumber,
                    SBin = r.SBin, HBin = r.HBin, Result = r.Result, Unit = item.Unit,
                    Value = valid ? value : null, RawValue = raw, Category = category, Color = WaferMapWindow.ColorForCategory(category)
                };
            }).ToList();
            return new WaferMapPage(wafer, dies, scale, item.Unit, true);
        }).ToList();
    }

    public static List<WaferMapPage> BinPages(CsvParseResult data, Func<DeviceInfo, string> selector) =>
        data.Wafers.Select(wafer => new WaferMapPage(wafer,
            data.Devices.Where(d => d.WaferId == wafer && d.X.HasValue && d.Y.HasValue).Select(d =>
            {
                string category = selector(d);
                return new WaferDie
                {
                    WaferId = wafer, X = d.X!.Value, Y = d.Y!.Value, Site = d.Site, SerialNumber = d.SerialNumber,
                    SBin = d.SBin, HBin = d.HBin, Result = d.Result,
                    Category = category, Color = WaferMapWindow.ColorForCategory(category)
                };
            }).ToList())).ToList();
}
