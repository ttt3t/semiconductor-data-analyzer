using System.Windows.Media;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ManagementTrendData(string[] Labels, List<WaferCategorySeries> Series);

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
        stages.Add(("COMBINED:" + kind, kind + " 综合良率"));
        var colors = BinPalette.Create(Enumerable.Range(2, stages.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var combinedBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)); combinedBrush.Freeze();
        return new(labels, stages.Select((s, i) => new WaferCategorySeries(s.Name,
            s.Id.StartsWith("COMBINED:") ? combinedBrush : colors[(i + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            scopes.Select(scope => lookup.GetValueOrDefault((scope.Lot, scope.Wafer, s.Id))).ToArray())).ToList());
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
