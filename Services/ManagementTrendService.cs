using System.Windows.Media;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public enum ManagementChartMode { YieldBars, StackedCounts, Pareto, RadialLines, CenterEdge, HorizontalBars }
public sealed record ManagementTrendData(string[] Labels, List<WaferCategorySeries> Series,
    ManagementChartMode Mode = ManagementChartMode.YieldBars, string AxisTitle = "良率（%）", double TargetYield = 85);

public static partial class ManagementTrendService
{
    public static ManagementTrendData CreateCategoryTrend(ManagedProduct product, string stageId, string filter, ManagementPeriod period = ManagementPeriod.Wafer)
    {
        var result = Categories(product, stageId, filter, period);
        var colors = BinPalette.Create(Enumerable.Range(2, result.CategoryRows.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new(result.Wafers.ToArray(), result.CategoryRows.Select((row, i) => new WaferCategorySeries(row.Category,
            colors[(i + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)], row.Percentages)).ToList());
    }

    public static ManagementTrendData Create(ManagedProduct product, string kind, string filter = "", bool byLot = false, ManagementPeriod period = ManagementPeriod.Wafer)
    {
        if (byLot && period == ManagementPeriod.Wafer) period = ManagementPeriod.Lot;
        var rows = YieldRows(product, filter, period).Where(r => r.Kind == kind).ToArray();
        (string Lot, string Wafer) Scope(ManagedYieldRow r) => period == ManagementPeriod.Wafer ? (r.Lot, r.Wafer) : (period == ManagementPeriod.Lot ? r.Lot : r.PeriodLabel, "");
        var scopes = rows.Select(Scope).Distinct().ToArray();
        var labels = scopes.Select(s => period != ManagementPeriod.Wafer ? s.Lot : kind == "FT" ? s.Lot + " / FT" : s.Lot + " / " + s.Wafer).ToArray();
        var lookup = rows.GroupBy(r => (Scope(r).Lot, Scope(r).Wafer, r.StageId)).ToDictionary(g => g.Key,
            g => ProductYieldService.AggregateYield(g));
        var stages = product.Flow.Where(s => s.Kind == kind).Select(s => (s.Id, Name: s.Label)).ToList();
        if (stages.Count > 1) stages.Add(("COMBINED:" + kind, kind + " 综合良率"));
        var colors = BinPalette.Create(Enumerable.Range(2, stages.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var combinedBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)); combinedBrush.Freeze();
        return new(labels, stages.Select((s, i) => new WaferCategorySeries(s.Name,
            s.Id.StartsWith("COMBINED:") ? combinedBrush : colors[(i + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            scopes.Select(scope => lookup.GetValueOrDefault((scope.Lot, scope.Wafer, s.Id))).ToArray(),
            ProductYieldService.AggregateYield(rows.Where(r => r.StageId == s.Id)))).ToList());
    }

    public static Brush FailColor(string bin)
    {
        string hex = bin switch { "2" => "#EF4444", "5" => "#EAB308", "4" => "#F97316", "8" => "#38BDF8", "21" => "#8B5CF6", "22" => "#22C55E", _ => "" };
        if (hex.Length == 0) return BinPalette.ColorForCategory(bin);
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; brush.Freeze(); return brush;
    }

    public static ManagementTrendData FailTrend(ManagedProduct product, string stageId, string type, string filter, ManagementPeriod period)
        => FailTrend(product, stageId, type, filter, period, null);

    public static ManagementTrendData DefectTrend(ManagedProduct product, string stageId, string filter, ManagementPeriod period)
    {
        var categories = ConfigLoader.Parse(product.ConfigText).SBinCategories
            .SelectMany(c => c.Value.Select(bin => (Bin: BinStatisticsService.NormalizeBin(bin), Category: c.Key)))
            .ToDictionary(c => c.Bin, c => c.Category, StringComparer.OrdinalIgnoreCase);
        return FailTrend(product, stageId, "SBIN", filter, period,
            bin => categories.GetValueOrDefault(BinStatisticsService.NormalizeBin(bin), "未分类"));
    }

    private static ManagementTrendData FailTrend(ManagedProduct product, string stageId, string type, string filter, ManagementPeriod period, Func<string, string>? classify)
    {
        var rows = product.Snapshot?.FailBins.Where(r => r.StageId == stageId && r.BinType == type && Matches(r.Lot, r.Wafer, filter)).ToArray() ?? Array.Empty<ManagedFailBinCount>();
        string Label(ManagedFailBinCount r) => period == ManagementPeriod.Wafer ? r.Lot + (r.Kind == "FT" ? " / FT" : " / " + r.Wafer) :
            period == ManagementPeriod.Lot ? r.Lot : r.Period.Length == 0 ? "未指定时间" : period == ManagementPeriod.Year ? r.Period[..4] : r.Period;
        // Keep zero-failure wafers visible using the yield population as the axis source.
        var yields = YieldRows(product, filter, period).Where(r => r.StageId == stageId).ToArray();
        string YieldLabel(ManagedYieldRow r) => period == ManagementPeriod.Wafer ? r.Lot + (r.Kind == "FT" ? " / FT" : " / " + r.Wafer) : period == ManagementPeriod.Lot ? r.Lot : r.PeriodLabel;
        var labels = yields.Select(YieldLabel).Concat(rows.Select(Label)).Distinct().ToArray();
        string Category(ManagedFailBinCount row) => classify?.Invoke(row.Bin) ?? row.Bin;
        var counts = rows.GroupBy(r => (Label: Label(r), Category: Category(r))).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        var bins = rows.GroupBy(Category).OrderByDescending(g => g.Sum(r => r.Count)).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToArray();
        return new(labels, bins.Select(bin => new WaferCategorySeries(classify == null ? "Bin " + bin : bin,
            classify == null ? FailColor(bin) : BinPalette.ColorForCategory(bin), labels.Select(label => (double?)counts.GetValueOrDefault((label, bin))).ToArray())).ToList(),
            ManagementChartMode.StackedCounts, "Fail 芯片数（颗）");
    }

    public static ManagementTrendData Pareto(ManagedProduct product, string stageId, string type, int take = int.MaxValue)
    {
        var bins = ManagementAnalytics.Bins(product, stageId, type).Take(take).ToArray();
        return new(bins.Select(b => "Bin " + b.Bin).ToArray(), new() { new(type + " Fail", type == "SBIN" ? Brushes.SteelBlue : Brushes.IndianRed,
            bins.Select(b => (double?)b.Percent).ToArray(), Counts: bins.Select(b => b.Count).ToArray()) }, ManagementChartMode.Pareto, "Fail 占比（%）");
    }

    public static ManagementTrendData RadialTrend(ManagedProduct product, string stageId)
    {
        var rows = product.Snapshot?.Radial.Where(r => r.StageId == stageId).ToArray() ?? Array.Empty<ManagedRadialCount>();
        return new(ManagementAnalytics.Regions, rows.GroupBy(r => (r.Lot, r.Wafer)).Select((g, index) => new WaferCategorySeries(g.Key.Lot + " / " + g.Key.Wafer,
            BinPalette.ColorForCategory((index + 2).ToString()), Enumerable.Range(0, 4).Select(region => g.FirstOrDefault(r => r.Region == region)?.Yield).ToArray())).ToList(),
            ManagementChartMode.RadialLines, "良率（%）");
    }

    public static ManagementTrendData CenterEdge(ManagedProduct product, string stageId)
    {
        var groups = (product.Snapshot?.Radial.Where(r => r.StageId == stageId) ?? Enumerable.Empty<ManagedRadialCount>()).GroupBy(r => (r.Lot, r.Wafer)).ToArray();
        return new(groups.Select(g => g.Key.Lot + " / " + g.Key.Wafer).ToArray(), new()
        {
            new("中心区", Brushes.SteelBlue, groups.Select(g => g.FirstOrDefault(r => r.Region == 0)?.Yield).ToArray()),
            new("边缘区", Brushes.IndianRed, groups.Select(g => g.FirstOrDefault(r => r.Region == 3)?.Yield).ToArray())
        }, ManagementChartMode.CenterEdge, "良率（%）");
    }

    public static ManagementTrendData RootCause(ManagedProduct product, string stageId)
    {
        var bins = ManagementAnalytics.Bins(product, stageId, "SBIN").Take(12).ToArray();
        return new(bins.Select(b => "Bin " + b.Bin + (b.Description.Length > 0 ? " · " + b.Description : "")).ToArray(),
            new() { new("SBIN Fail", Brushes.SteelBlue, bins.Select(b => (double?)b.Percent).ToArray(), Counts: bins.Select(b => b.Count).ToArray()) },
            ManagementChartMode.HorizontalBars, "Fail 占比（%）");
    }

    public static WaferStatisticsResult Categories(ManagedProduct product, string stageId, string filter, ManagementPeriod period = ManagementPeriod.Wafer)
    {
        var source = CategoryRows(product, filter, period).Where(r => r.StageId == stageId).ToArray();
        (string Lot, string Wafer) Scope(ManagedCategoryCount r) => period == ManagementPeriod.Wafer ? (r.Lot, r.Wafer) : (period == ManagementPeriod.Lot ? r.Lot : r.PeriodLabel, "");
        var scopes = source.Select(Scope).Distinct().ToArray();
        var labels = scopes.Select(s => period != ManagementPeriod.Wafer ? s.Lot : source[0].Kind == "FT" ? s.Lot + " / FT" : s.Lot + " / " + s.Wafer).ToList();
        var categories = source.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lookup = source.ToDictionary(r => (Scope(r).Lot, Scope(r).Wafer, r.Category));
        var totals = scopes.Select(s => source.First(r => Scope(r) == s).Total).ToArray();
        return new(labels, totals, totals, new())
        {
            CategoryRows = categories.Select(category => new WaferBinRow("", category,
                scopes.Select(s => lookup.GetValueOrDefault((s.Lot, s.Wafer, category))?.Count ?? 0).ToArray(),
                scopes.Select((s, i) => totals[i] == 0 ? (double?)null : lookup.GetValueOrDefault((s.Lot, s.Wafer, category))?.Percent ?? 0).ToArray())).ToList()
        };
    }

    public static ManagedCategoryCount[] CategoryRows(ManagedProduct product, string filter = "", ManagementPeriod period = ManagementPeriod.Wafer)
    {
        if (product.Snapshot is not { } snapshot) return Array.Empty<ManagedCategoryCount>();
        if (period == ManagementPeriod.Wafer) return snapshot.Categories.Where(r => Matches(r.Lot, r.Wafer, filter)).ToArray();
        var source = period == ManagementPeriod.Lot ? snapshot.Categories : snapshot.MonthlyCategories.Count > 0 ? snapshot.MonthlyCategories : snapshot.Categories;
        var scoped = source.Where(r => Matches(r.Lot, r.Wafer, filter)).ToArray();
        string Key(ManagedCategoryCount row) => period == ManagementPeriod.Lot ? row.Lot : PeriodKey(row.Period, period);
        // An unconfigured category may be absent in a wafer. Its zero count must still use that wafer's denominator.
        var totals = scoped.GroupBy(r => (Period: Key(r), r.Kind, r.StageId))
            .ToDictionary(g => g.Key, g => g.GroupBy(r => (r.Period, r.Lot, r.Wafer)).Sum(scope => scope.First().Total));
        return scoped
            .GroupBy(r => (Period: Key(r), r.Kind, r.StageId, r.Category))
            .OrderBy(g => g.Key.Period.Length == 0).ThenBy(g => g.Key.Period, StringComparer.Ordinal)
            .Select(g => new ManagedCategoryCount { Period = period == ManagementPeriod.Lot ? "" : g.Key.Period, Kind = g.Key.Kind, StageId = g.Key.StageId,
                Lot = period == ManagementPeriod.Lot ? g.Key.Period : "全部批次", Wafer = g.Key.Kind == "FT" ? "" : "全部晶圆", Category = g.Key.Category,
                Count = g.Sum(r => r.Count), Total = totals[(g.Key.Period, g.Key.Kind, g.Key.StageId)] }).ToArray();
    }
}
