using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public readonly record struct ExportTestScope(string ItemId, string? Site, string? Wafer);

internal sealed record AnalyzerExportSelection(CsvParseResult Source, ExportTestScope[]? TestScopes)
{
    public string[] Wafers()
    {
        if (TestScopes == null || TestScopes.Any(scope => scope.Wafer == null)) return Source.Wafers.ToArray();
        var wafers = TestScopes.Select(scope => scope.Wafer).ToHashSet(StringComparer.Ordinal);
        return Source.Wafers.Where(wafer => wafers.Contains(wafer)).ToArray();
    }

    public (CsvParseResult Data, string[] Wafers) Prepare(string[] wafers)
    {
        if (TestScopes == null) return (Source, wafers);
        AnalysisOperation.CheckCancellation();
        var selectedWafers = wafers.ToHashSet(StringComparer.Ordinal);
        var testIdsByScope = TestScopes.GroupBy(scope => (scope.Site, scope.Wafer))
            .ToDictionary(group => group.Key, group => group.Select(scope => scope.ItemId).ToHashSet(StringComparer.Ordinal));
        bool combineSites = TestScopes.All(scope => scope.Site == null);
        bool combineWafers = TestScopes.All(scope => scope.Wafer == null);
        var records = new List<CsvRecord>();
        var tests = new HashSet<string>(StringComparer.Ordinal);
        var representedScopes = new HashSet<(string? Site, string? Wafer)>();
        foreach (var record in Source.Records)
        {
            AnalysisOperation.CheckCancellation();
            if (!selectedWafers.Contains(record.WaferId)) continue;
            string? site = combineSites ? null : string.IsNullOrWhiteSpace(record.Site) ? "(空)" : record.Site;
            string? wafer = combineWafers ? null : record.WaferId;
            if (!testIdsByScope.TryGetValue((site, wafer), out var allowed)) continue;
            if (representedScopes.Add((site, wafer))) tests.UnionWith(allowed);
            var measurements = record.Measurements.SelectKeys(allowed.Contains);
            if (ReferenceEquals(measurements, record.Measurements)) records.Add(record);
            else
            {
                var copy = DatasetService.CloneRecord(record);
                copy.Measurements = measurements;
                records.Add(copy);
            }
        }
        if (records.Count == 0) throw new InvalidDataException("所选晶圆和当前筛选条件没有可导出的记录。");
        var data = DatasetService.CreateView(Source, records, tests);
        return (data, data.Wafers.ToArray());
    }
}
