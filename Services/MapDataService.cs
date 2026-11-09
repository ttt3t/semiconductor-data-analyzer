using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class MapDataService
{
    public static List<WaferMapPage> PfPages(CsvParseResult data, TestItemData item, string? site, AnalyzerConfig? config = null)
    {
        if (config?.PreferGoodRetest == true)
        {
            data = DatasetService.SelectRetests(data, RetestMode.Last, config);
            item = data.Items.Single(i => i.Id == item.Id);
        }
        bool IncludeSite(string value) => site == null || (string.IsNullOrWhiteSpace(value) ? "(空)" : value) == site;
        // Pool all wafer measurements once. A wafer switch must not change the meaning of a color.
        // Retain the PF map's existing valid-measurement and Site population.
        var scale = HeatmapScale.Create(item.Values.Where(v => IncludeSite(v.Site)).Select(v => v.Value * item.DisplayFactor));
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
                    SBin = r.SBin, HBin = r.HBin, Result = r.Result, Unit = item.ShownUnit,
                    Value = valid ? value * item.DisplayFactor : null, RawValue = MeasurementDisplay.ExportText(item, raw), Category = category, Color = WaferMapWindow.ColorForCategory(category)
                };
            }).ToList();
            return new WaferMapPage(wafer, dies, scale, item.ShownUnit, true,
                dies.Count(d => d.Category == "PASS"), "当前测项 Pass / 有坐标且含该测项的芯片（含无效测量）");
        }).ToList();
    }

    public static List<WaferMapPage> BinPages(CsvParseResult data, bool soft, AnalyzerConfig? config = null)
    {
        var colors = BinPalette.Create(data.Records.Select(r => soft ? r.SBin : r.HBin));
        if (config?.PreferGoodRetest == true) data = DatasetService.SelectRetests(data, RetestMode.Last, config);
        var classifier = new HBinClassifier(config ?? new AnalyzerConfig());
        return data.Wafers.Select(wafer =>
        {
            var dies = data.Devices.Where(d => d.WaferId == wafer && d.X.HasValue && d.Y.HasValue).Select(d =>
            {
                string category = BinPalette.Key(soft ? d.SBin : d.HBin);
                return new WaferDie
                {
                    WaferId = wafer, X = d.X!.Value, Y = d.Y!.Value, Site = d.Site, SerialNumber = d.SerialNumber,
                    SBin = d.SBin, HBin = d.HBin, Result = d.Result,
                    Category = category, Color = colors[category]
                };
            }).ToList();
            return new WaferMapPage(wafer, dies, PassCount: dies.Count(d => classifier.Classify(d.HBin) == OriginalDisposition.Pass),
                YieldBasis: "配置的 HBIN Pass / 图中有坐标芯片；不含无坐标记录");
        }).ToList();
    }
}
