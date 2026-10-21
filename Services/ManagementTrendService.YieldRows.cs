using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class ManagementTrendService
{
    public static bool Matches(string lot, string wafer, string filter) => filter.Length == 0 ||
        lot.Contains(filter, StringComparison.OrdinalIgnoreCase) || wafer.Contains(filter, StringComparison.OrdinalIgnoreCase);

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
                row.SourceFileIds = g.SelectMany(r => r.SourceFileIds).ToHashSet(StringComparer.Ordinal);
                row.Source = string.Join(" / ", g.Select(r => r.Source).Distinct());
                row.Note = !row.HasSampleCount && g.Count() > 1 ? "缺少样本数，无法加权汇总。" : period == ManagementPeriod.Lot ? "按芯片数加权。" : "按芯片数加权；综合按末次已测必测步骤时间归属。";
                if (g.Any(r => !r.CanCalculateYield)) row.Note += "存在无法计算的原始汇总，详见晶圆明细。";
                return row;
            }).ToArray();
    }

}
