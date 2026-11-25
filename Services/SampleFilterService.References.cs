using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class SampleFilterService
{
    private readonly record struct Interval(double? Minimum, double? Maximum, double? Reference = null, double? Sigma = null)
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

    private sealed record ResolvedRange(Interval? Fixed, Dictionary<SampleLocation, Interval>? ByWafer = null)
    {
        public Interval? ForRecord(CsvRecord? record) => ByWafer == null ? Fixed :
            record != null && ByWafer.TryGetValue(SampleLocation.FromRecord(record), out var interval) ? interval : null;
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
        Interval Around(double center, double? sigma = null)
        {
            double scale = sigma ?? (deviation.Calculation == SampleCalculation.Relative ? Math.Abs(center) / 100 : 1);
            double? low = center - scale * deviation.Negative;
            double? high = center + scale * (sigma.HasValue ? deviation.Negative : deviation.Positive);
            if (!double.IsFinite(center) || Invalid(low) || Invalid(high)) throw Error("基准或计算范围超出有效数值。");
            return new(low, high, center, sigma);
        }

        if (deviation.Calculation == SampleCalculation.StandardDeviation)
        {
            if (!deviation.Negative.HasValue || Invalid(deviation.Negative) || deviation.Negative < 0)
                throw Error("请填写非负、有限的标准差倍数。");
            var (mean, sigma) = MeanAndSigma(records, item.Id) ?? throw Error("当前样本没有可计算标准差的有效值。");
            return new(Around(mean, sigma));
        }
        if (deviation.Calculation is not (SampleCalculation.Relative or SampleCalculation.Absolute) ||
            (!deviation.Negative.HasValue && !deviation.Positive.HasValue) || Invalid(deviation.Negative) ||
            Invalid(deviation.Positive) || deviation.Negative < 0 || deviation.Positive < 0)
            throw Error("请填写至少一个非负、有限的偏差。");

        if (deviation.Baseline == SampleBaseline.WaferMean)
        {
            var means = new Dictionary<SampleLocation, RunningMean>();
            foreach (var record in records)
            {
                AnalysisOperation.CheckCancellation();
                if (ReadValue(record, item.Id) is not double value) continue;
                var wafer = SampleLocation.FromRecord(record);
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

    private static (double Mean, double Sigma)? MeanAndSigma(IReadOnlyList<CsvRecord> records, string id)
    {
        var mean = new RunningMean();
        double scale = 0;
        foreach (var record in records)
        {
            AnalysisOperation.CheckCancellation();
            if (ReadValue(record, id) is not double value) continue;
            mean = mean.Add(value);
            scale = Math.Max(scale, Math.Abs(value));
        }
        if (mean.Count == 0) return null;
        if (scale == 0) return (0, 0);

        // Scale differences before squaring so finite measurements do not overflow the variance.
        double squared = 0;
        foreach (var record in records)
        {
            AnalysisOperation.CheckCancellation();
            if (ReadValue(record, id) is not double value) continue;
            double difference = value - mean.Mean;
            double scaled = double.IsFinite(difference) ? difference / scale : value / scale - mean.Mean / scale;
            squared += scaled * scaled;
        }
        return (mean.Mean, Math.Sqrt(squared / mean.Count) * scale);
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
        AnalyzerConfig config, CsvRecord? currentRun)
    {
        var items = data.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        IReadOnlyList<CsvRecord> selected = rules.Any(rule => rule.Deviation is { } options &&
            (options.Calculation == SampleCalculation.StandardDeviation ||
            options.Baseline is SampleBaseline.Mean or SampleBaseline.Median or SampleBaseline.WaferMean))
            ? DatasetService.SelectRetestRecords(data.Records, mode, config) : Array.Empty<CsvRecord>();
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
                if (interval.Sigma is double sigma) range += " · σ " + Number(sigma);
                if (resolved.ByWafer != null && currentRun != null)
                    range += " · " + string.Join(" / ", new[] { currentRun.Lot.Trim(), currentRun.WaferId }.Where(s => s.Length > 0));
                previews[rule.ItemId] = range;
            }
            catch (InvalidDataException ex) { previews[rule.ItemId] = ex.Message; }
        }
        return previews;
    }
}
