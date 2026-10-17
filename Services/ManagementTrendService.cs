using System.Windows.Media;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ManagementTrendData(string[] Labels, List<WaferCategorySeries> Series);

public static class ManagementTrendService
{
    public static ManagementTrendData CreateCategoryTrend(ManagedProduct product, string stageId, string filter, ManagementPeriod period = ManagementPeriod.Wafer)
    {
        var result = Categories(product, stageId, filter, period);
        var colors = BinPalette.Create(Enumerable.Range(2, result.CategoryRows.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new(result.Wafers.ToArray(), result.CategoryRows.Select((row, i) => new WaferCategorySeries(row.Category,
            colors[(i + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)], row.Percentages)).ToList());
    }
    public static bool Matches(string lot, string wafer, string filter) => filter.Length == 0 ||
        lot.Contains(filter, StringComparison.OrdinalIgnoreCase) || wafer.Contains(filter, StringComparison.OrdinalIgnoreCase);

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

    private static string PeriodKey(string month, ManagementPeriod period) => period == ManagementPeriod.Year && month.Length >= 4 ? month[..4] : month;

    public static bool HasMultipleSteps(ManagedProduct product, string kind) => product.Flow.Count(s => s.Kind == kind) > 1;

    public static ManagedYieldRow[] DetailYieldRows(ManagedProduct product, string filter = "", ManagementPeriod period = ManagementPeriod.Wafer) =>
        YieldRows(product, filter, period).Where(r => !r.IsCombined || HasMultipleSteps(product, r.Kind)).ToArray();

    public static ManagedYieldRow[] YieldRows(ManagedProduct product, string filter = "", ManagementPeriod period = ManagementPeriod.Wafer)
    {
        if (product.Snapshot is not { } snapshot) return Array.Empty<ManagedYieldRow>();
        if (period == ManagementPeriod.Wafer) return snapshot.Yields.Where(r => Matches(r.Lot, r.Wafer, filter)).ToArray();
        var source = period == ManagementPeriod.Lot ? snapshot.Yields : snapshot.MonthlyYields.Count > 0 ? snapshot.MonthlyYields : snapshot.Yields;
        return source.Where(r => Matches(r.Lot, r.Wafer, filter))
            .GroupBy(r => (Period: period == ManagementPeriod.Lot ? r.Lot : PeriodKey(r.Period, period), r.Kind, r.StageId))
            .OrderBy(g => g.Key.Period.Length == 0).ThenBy(g => g.Key.Period, StringComparer.Ordinal)
            .Select(g =>
            {
                var row = ProductCatalogStore.Clone(g.First()); row.Period = period == ManagementPeriod.Lot ? "" : g.Key.Period;
                row.Lot = period == ManagementPeriod.Lot ? g.Key.Period : "全部批次"; row.Wafer = row.Kind == "FT" ? "" : "全部晶圆";
                row.HasSampleCount = g.All(r => r.HasSampleCount); row.HasDispositionCounts = g.All(r => r.HasDispositionCounts);
                row.CanCalculateYield = g.All(r => r.CanCalculateYield) && (g.Count() == 1 || row.HasSampleCount);
                row.SpecifiedYield = ProductYieldService.AggregateYield(g);
                row.Total = g.Sum(r => r.Total); row.Good = g.Sum(r => r.Good); row.Failed = g.Sum(r => r.Failed);
                row.Pending = g.Sum(r => r.Pending); row.Pass = g.Sum(r => r.Pass); row.PartialPass = g.Sum(r => r.PartialPass);
                row.MissingTemperature = g.Sum(r => r.MissingTemperature); row.MissingIdentity = g.Sum(r => r.MissingIdentity);
                row.Source = string.Join(" / ", g.Select(r => r.Source).Distinct());
                row.Note = !row.HasSampleCount && g.Count() > 1 ? "缺少样本数，无法加权汇总。" : period == ManagementPeriod.Lot ? "按芯片数加权。" : "按芯片数加权；综合按末次已测必测步骤时间归属。";
                if (g.Any(r => !r.CanCalculateYield)) row.Note += "存在无法计算的原始汇总，详见晶圆明细。";
                return row;
            }).ToArray();
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
