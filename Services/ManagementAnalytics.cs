using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ManagementBinSummary(string Bin, string Description, int Count, double Percent, double Cumulative);
public sealed record ManagementChartLayout(double ColumnWidth, double HeaderWidth, double ChartHeight,
    double RowHeight = 22, double HeaderHeight = 34, double RightPadding = 18);
public sealed record ManagementReportOptions(HashSet<ManagementScope> Scopes, double TargetYield = 85,
    ManagementCountDisplay BinDisplay = ManagementCountDisplay.Count, ManagementCountDisplay DefectDisplay = ManagementCountDisplay.Count,
    string BinType = "HBIN", ManagementChartLayout? BinLayout = null, ManagementChartLayout? DefectLayout = null,
    ManagementChartLayout? YieldLayout = null);

public static class ManagementAnalytics
{
    public static readonly string[] Regions = { "中心 ≤40%", "中环 40–70%", "外环 70–90%", "边缘 >90%" };

    public static ManagementBinSummary[] Bins(ManagedProduct product, string stageId, string type, string filter = "")
    {
        var rows = product.Snapshot?.FailBins.Where(r => r.StageId == stageId && r.BinType == type && Matches(r.Lot, r.Wafer, filter)).ToArray()
            ?? Array.Empty<ManagedFailBinCount>();
        int total = rows.Sum(r => r.Count), cumulative = 0;
        return rows.GroupBy(r => r.Bin).Select(g => (Bin: g.Key, Description: g.Select(r => r.Description).FirstOrDefault(s => s.Length > 0) ?? "", Count: g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Bin, StringComparer.Ordinal).Select(r =>
            {
                cumulative += r.Count;
                return new ManagementBinSummary(r.Bin, r.Description, r.Count, total == 0 ? 0 : r.Count * 100.0 / total, total == 0 ? 0 : cumulative * 100.0 / total);
            }).ToArray();
    }

    public static bool Matches(string lot, string wafer, string filter) => filter.Length == 0 ||
        lot.Contains(filter, StringComparison.OrdinalIgnoreCase) || wafer.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static ManagementScope[] Scopes(ManagedProduct product) => product.Snapshot?.Yields
        .Select(r => new ManagementScope(r.Kind, r.Lot, r.Wafer)).Distinct().OrderBy(s => s.Lot, StringComparer.Ordinal)
        .ThenBy(s => s.Kind).ThenBy(s => s.Wafer, StringComparer.Ordinal).ToArray() ?? Array.Empty<ManagementScope>();

    public static ManagedProduct Select(ManagedProduct source, HashSet<ManagementScope> scopes)
    {
        if (scopes.Count == 0) throw new InvalidDataException("请至少勾选一个批次或晶圆。");
        var product = ProductCatalogStore.Clone(source);
        if (product.Snapshot is not { } snapshot) throw new InvalidDataException("请先计算产品良率。");
        bool Selected(string kind, string lot, string wafer) => scopes.Contains(new(kind, lot, wafer));
        snapshot.Yields = snapshot.Yields.Where(r => Selected(r.Kind, r.Lot, r.Wafer)).ToList();
        snapshot.MonthlyYields = snapshot.MonthlyYields.Where(r => Selected(r.Kind, r.Lot, r.Wafer)).ToList();
        snapshot.Categories = snapshot.Categories.Where(r => Selected(r.Kind, r.Lot, r.Wafer)).ToList();
        snapshot.MonthlyCategories = snapshot.MonthlyCategories.Where(r => Selected(r.Kind, r.Lot, r.Wafer)).ToList();
        snapshot.FailBins = snapshot.FailBins.Where(r => Selected(r.Kind, r.Lot, r.Wafer)).ToList();
        snapshot.Radial = snapshot.Radial.Where(r => Selected("CP", r.Lot, r.Wafer)).ToList();
        var fileIds = snapshot.Yields.SelectMany(r => r.SourceFileIds).ToHashSet(StringComparer.Ordinal);
        var failed = snapshot.Files.Where(r => r.Error.Length > 0).Select(r => r.FileId).ToHashSet(StringComparer.Ordinal);
        product.Files = product.Files.Where(f => fileIds.Contains(f.Id) || failed.Contains(f.Id) && scopes.Any(s => s.Lot == f.Lot &&
            (f.WaferOverride.Length == 0 || s.Wafer == f.WaferOverride))).ToList();
        var retained = product.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        snapshot.Files = snapshot.Files.Where(f => retained.Contains(f.FileId)).ToList();
        return product;
    }
}
