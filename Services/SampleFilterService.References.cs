using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class SampleFilterService
{
    private readonly record struct Interval(double? Minimum, double? Maximum, double? Reference = null)
    {
        public bool Matches(double value, bool outside)
        {
            if (Minimum > Maximum)
                return outside ? value < Maximum!.Value || value > Minimum!.Value
                    : value <= Maximum!.Value || value >= Minimum!.Value;
            bool inside = (!Minimum.HasValue || value >= Minimum.Value) &&
                (!Maximum.HasValue || value <= Maximum.Value);
            return outside ? !inside : inside;
        }
    }

    private sealed record ResolvedRange(Interval? Fixed, Dictionary<(string Lot, string Wafer), Interval>? ByWafer = null)
    {
        public Interval? ForRecord(CsvRecord? record) => ByWafer == null ? Fixed :
            record != null && ByWafer.TryGetValue(WaferKey(record), out var interval) ? interval : null;
    }

    private readonly record struct RunningMean(int Count, double Mean)
    {
        public RunningMean Add(double value)
        {
            if (Count == 0) return new(1, value);
            double divisor = Count + 1.0;
            double difference = value - Mean;
            double next = double.IsFinite(difference) ? Mean + difference / divisor :
                Mean * (Count / divisor) + value / divisor;
            return new(Count + 1, next);
        }
    }

    private static (string Lot, string Wafer) WaferKey(CsvRecord record) => (record.Lot.Trim(), record.WaferId);

    internal static double? ReadValue(CsvRecord record, string id) =>
        record.Measurements.TryGetText(id, out var raw) && DatasetService.TryValue(raw, out double value) ? value : null;

    private static ResolvedRange Resolve(TestItemData? item, SampleRange rule, IReadOnlyList<CsvRecord> records)
    {
        if (item == null) throw new InvalidDataException("筛选项已不存在，请重新打开数据筛选。");
        InvalidDataException Error(string message) => new(item.DisplayName + "：" + message);
        bool Invalid(double? value) => value.HasValue && !double.IsFinite(value.Value);
        if (rule.Deviation is not { } deviation)
        {
            if ((!rule.Minimum.HasValue && !rule.Maximum.HasValue) || Invalid(rule.Minimum) || Invalid(rule.Maximum))
                throw Error("请填写至少一个有限的范围边界。");
            return new(new Interval(rule.Minimum, rule.Maximum));
        }
        if (deviation.Calculation is not (SampleCalculation.Relative or SampleCalculation.Absolute) ||
            (!deviation.Negative.HasValue && !deviation.Positive.HasValue) || Invalid(deviation.Negative) ||
            Invalid(deviation.Positive) || deviation.Negative < 0 || deviation.Positive < 0)
            throw Error("请填写至少一个非负、有限的偏差。");

        Interval Around(double center)
        {
            double scale = deviation.Calculation == SampleCalculation.Relative ? Math.Abs(center) / 100 : 1;
            double? low = center - scale * deviation.Negative;
            double? high = center + scale * deviation.Positive;
            if (!double.IsFinite(center) || Invalid(low) || Invalid(high)) throw Error("基准或计算范围超出有效数值。");
            return new(low, high, center);
        }

        if (deviation.Baseline == SampleBaseline.WaferMean)
        {
            var means = new Dictionary<(string Lot, string Wafer), RunningMean>();
            foreach (var record in records)
            {
                AnalysisOperation.CheckCancellation();
                if (ReadValue(record, item.Id) is not double value) continue;
                var wafer = WaferKey(record);
                means[wafer] = means.GetValueOrDefault(wafer).Add(value);
            }
            if (means.Count == 0) throw Error("当前数据没有可用的 Wafer 均值。");
            return new(null, means.ToDictionary(pair => pair.Key, pair => Around(pair.Value.Mean)));
        }

        double? baseline = deviation.Baseline switch
        {
            SampleBaseline.Custom or SampleBaseline.Golden => deviation.Reference,
            SampleBaseline.SpecCenter when item.LowLimit is double low && item.HighLimit is double high => low / 2 + high / 2,
            SampleBaseline.Mean => Mean(records, item.Id),
            SampleBaseline.Median => Median(records, item.Id),
            _ => null
        };
        if (baseline is not double center) throw Error(deviation.Baseline switch
        {
            SampleBaseline.Custom => "请填写自定义基准值。",
            SampleBaseline.Golden => "请使用含该测项有效值的 Run 作为 Golden 样品。",
            SampleBaseline.SpecCenter => "计算 Spec 中心值需要上下限。",
            _ => "当前样本没有可用的基准值。"
        });
        return new(Around(center));
    }

    private static double? Mean(IReadOnlyList<CsvRecord> records, string id)
    {
        var mean = new RunningMean();
        foreach (var record in records)
        {
            AnalysisOperation.CheckCancellation();
            if (ReadValue(record, id) is double value) mean = mean.Add(value);
        }
        return mean.Count == 0 ? null : mean.Mean;
    }

    private static double? Median(IReadOnlyList<CsvRecord> records, string id)
    {
        var values = new List<double>();
        foreach (var record in records)
        {
            AnalysisOperation.CheckCancellation();
            if (ReadValue(record, id) is double value) values.Add(value);
        }
        if (values.Count == 0) return null;
        values.Sort();
        AnalysisOperation.CheckCancellation();
        int mid = values.Count / 2;
        return values.Count % 2 == 0 ? values[mid - 1] / 2 + values[mid] / 2 : values[mid];
    }

    internal static Dictionary<string, string> Preview(CsvParseResult data, IReadOnlyList<SampleRange> rules, RetestMode mode,
        AnalyzerConfig config, IReadOnlyList<string> sBins, CsvRecord? currentRun)
    {
        var items = data.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        IReadOnlyList<CsvRecord> selected = rules.Any(rule => rule.Deviation?.Baseline is SampleBaseline.Mean or SampleBaseline.Median or SampleBaseline.WaferMean)
            ? DatasetService.SelectRetestRecords(EligibleRecords(data, sBins), mode, config) : Array.Empty<CsvRecord>();
        var previews = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            AnalysisOperation.CheckCancellation();
            var item = items.GetValueOrDefault(rule.ItemId);
            try
            {
                var resolved = Resolve(item, rule, selected);
                if (resolved.ForRecord(currentRun) is not { } interval)
                {
                    previews[rule.ItemId] = "当前 Run 所在晶圆无基准";
                    continue;
                }
                string Number(double value) => (value * item!.DisplayFactor).ToString("G8", CultureInfo.InvariantCulture);
                string range = (interval.Minimum, interval.Maximum) switch
                {
                    (double low, double high) when low > high && !rule.Outside => $"≤ {Number(high)} 或 ≥ {Number(low)}",
                    (double low, double high) => $"{Number(Math.Min(low, high))} ~ {Number(Math.Max(low, high))}",
                    (double low, null) => (rule.Outside ? "< " : "≥ ") + Number(low),
                    (null, double high) => (rule.Outside ? "> " : "≤ ") + Number(high),
                    _ => ""
                };
                if (rule.Outside && interval.Minimum.HasValue && interval.Maximum.HasValue) range = "范围外 " + range;
                if (interval.Reference is double center) range += " · 基准 " + Number(center);
                if (resolved.ByWafer != null && currentRun != null)
                    range += " · " + string.Join(" / ", new[] { currentRun.Lot.Trim(), currentRun.WaferId }.Where(s => s.Length > 0));
                previews[rule.ItemId] = range;
            }
            catch (InvalidDataException ex) { previews[rule.ItemId] = ex.Message; }
        }
        return previews;
    }
}
