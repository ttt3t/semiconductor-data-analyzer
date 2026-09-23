using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Models;

public enum MeasurementState : byte { Unmeasured, Measured, NotApplicable, Invalid }
public enum ChipVerdict { Good, Bad, Pending, Unknown }
public sealed record SimulationRule(int Item, bool Included, bool Enabled, double? Lower, double? Upper, int Stage);
public sealed record PredictionOptions(bool IncludePartial = false, int MinimumReferences = 20,
    int MaximumReferences = 100, double MaximumDistance = 1.5);

public sealed class SimulationRuleRow : INotifyPropertyChanged
{
    public int Item { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public double? OriginalLower { get; init; }
    public double? OriginalUpper { get; init; }
    public string OriginalLimits => $"{Number(OriginalLower)} ～ {Number(OriginalUpper)}";
    public string Recommendation { get; set; } = "";
    public int GoodViolations { get; set; }
    public int BadViolations { get; set; }
    public int TestCount { get; init; }
    public double? OriginalYield { get; init; }
    private SimulationItemStatistics? _statistics;
    public double? PredictedYield => _statistics?.SimulatedYield;
    public string PredictedYieldText => _statistics == null ? "待计算"
        : !_statistics.Included ? "未纳入" : !_statistics.Enabled ? "已关闭"
        : PredictedYield?.ToString("P2", CultureInfo.CurrentCulture) ?? "—";
    public void ApplyStatistics(SimulationItemStatistics? statistics)
    {
        if (Equals(_statistics, statistics)) return;
        _statistics = statistics;
        PropertyChanged?.Invoke(this, new(nameof(PredictedYield)));
        PropertyChanged?.Invoke(this, new(nameof(PredictedYieldText)));
    }
    private bool _included, _enabled = true;
    private string _lower = "", _upper = "", _stage = "1";
    public bool Included { get => _included; set => Set(ref _included, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Lower { get => _lower; set => Set(ref _lower, value); }
    public string Upper { get => _upper; set => Set(ref _upper, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(property)); }
    public static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    public SimulationRule Capture()
    {
        double? Bound(string text) => string.IsNullOrWhiteSpace(text) ? null : DatasetService.TryValue(text, out double v)
            ? v : throw new ArgumentException($"{Name}：上下限必须为有限数字或留空。");
        var lower = Bound(Lower); var upper = Bound(Upper);
        if (lower > upper) throw new ArgumentException($"{Name}：下限不能大于上限。");
        if (!int.TryParse(Stage, out int stage) || stage < 1) throw new ArgumentException($"{Name}：阶段必须为正整数。");
        return new(Item, Included, Enabled, lower, upper, stage);
    }
}

/// <summary>Per-item observed yields; missing results never become presumed passes.</summary>
public sealed record SimulationItemStatistics(int Item, int Tested, int OriginalPassed, int SimulatedPassed,
    bool HasOriginalLimits, bool Included, bool Enabled)
{
    public double? OriginalYield => Tested > 0 && HasOriginalLimits ? (double)OriginalPassed / Tested : null;
    public double? SimulatedYield => Tested > 0 && Included && Enabled ? (double)SimulatedPassed / Tested : null;
}

public sealed record PredictionEvidence(double? Probability, int References, int JointPass, int JointFail, int Unknown,
    int OriginalBad, int SameLot, bool Extrapolated, string Reason, string Features)
{
    public double? Lower => References == 0 ? null : (double)JointPass / References;
    public double? Upper => References == 0 ? null : (double)(JointPass + Unknown) / References;
    public string Text => (Extrapolated ? "外推估计；" : "") + Reason +
        $"；参考 {References}（联合通过 {JointPass} / 失败 {JointFail} / 未知 {Unknown}，原始不良 {OriginalBad}，同批 {SameLot}）" +
        (References > 0 ? $"；参考联合通过率范围 {Lower:P1}～{Upper:P1}" : "") + (Features.Length > 0 ? "；前段项：" + Features : "");
}

public sealed record SimulationChipResult(int Index, CsvRecord Record, OriginalDisposition Original,
    ChipVerdict Baseline, ChipVerdict Simulated, int Measured, int Missing, int NotApplicable, int Invalid,
    int[] MissingItems, PredictionEvidence? Evidence, bool OriginalGood)
{
    public string Wafer => Record.WaferId;
    public string SN => Record.SerialNumber;
    public string Site => Record.Site;
    public string XY => $"{Record.X}, {Record.Y}";
    public string HBIN => Record.HBin;
    public string BaselineText => VerdictText(Baseline);
    public string Status => Simulated == ChipVerdict.Good ? OriginalGood ? "确认良品" : "确认恢复"
        : Simulated == ChipVerdict.Bad ? OriginalGood ? "新增不良" : "确认不良"
        : Evidence?.Probability != null ? "待预测（概率估计）" : "无法估计";
    public string Probability => Evidence?.Probability is double p ? p.ToString("P2") : "—";
    public string Note => Evidence?.Text ?? (Simulated == ChipVerdict.Unknown ? "无有效判定依据 / 无效数据 / 复测上下文冲突" : "");
    public static string VerdictText(ChipVerdict v) => v switch
    { ChipVerdict.Good => "良品", ChipVerdict.Bad => "不良", ChipVerdict.Pending => "缺测", _ => "未知" };
}
public sealed record MissingGroupRow(string Items, int Chips, int Estimated, int Unestimable, double ExpectedGood);
public sealed record SimulationResult(IReadOnlyList<SimulationChipResult> Chips, IReadOnlyList<MissingGroupRow> Groups)
{
    public IReadOnlyList<SimulationItemStatistics> Items { get; init; } = Array.Empty<SimulationItemStatistics>();
    public int Total => Chips.Count;
    public int Pass => Chips.Count(c => c.Original == OriginalDisposition.Pass);
    public int PartialPass => Chips.Count(c => c.Original == OriginalDisposition.PartialPass);
    public int BaselineGood => Chips.Count(c => c.Baseline == ChipVerdict.Good);
    public int ConfirmedGood => Chips.Count(c => c.Simulated == ChipVerdict.Good);
    public int Recovered => Chips.Count(c => !c.OriginalGood && c.Simulated == ChipVerdict.Good);
    public int NewBad => Chips.Count(c => c.OriginalGood && c.Simulated == ChipVerdict.Bad);
    public int RuleRecovered => Chips.Count(c => c.Baseline == ChipVerdict.Bad && c.Simulated == ChipVerdict.Good);
    public int RuleNewBad => Chips.Count(c => c.Baseline == ChipVerdict.Good && c.Simulated == ChipVerdict.Bad);
    public int Unestimable => Chips.Count(c => c.Simulated is ChipVerdict.Pending or ChipVerdict.Unknown && c.Evidence?.Probability == null);
    public double ExpectedGood => ConfirmedGood + Chips.Sum(c => c.Evidence?.Probability ?? 0);
    public string Summary
    {
        get
        {
            string Rate(double n) => Total == 0 ? "—" : (n / Total).ToString("P2");
            int mismatch = Chips.Count(c => c.Baseline is ChipVerdict.Good or ChipVerdict.Bad && (c.Baseline == ChipVerdict.Good) != c.OriginalGood);
            int unknownBase = Chips.Count(c => c.Baseline is ChipVerdict.Pending or ChipVerdict.Unknown);
            return $"芯片 {Total}   原始 Pass {Rate(Pass)}   Pass + PartialPass {Rate(Pass + PartialPass)}\n" +
                $"基准重算 {Rate(BaselineGood)}（确认良品 {BaselineGood}；与目标 HBIN 判定相反 {mismatch}；未确定 {unknownBase}）   模拟确认良率 {Rate(ConfirmedGood)}\n" +
                $"{(Unestimable > 0 ? "已覆盖部分概率预计良率" : "概率预计良率")} {Rate(ExpectedGood)}   预计良品 {ExpectedGood:F2} = 确认 {ConfirmedGood} + 概率和 {ExpectedGood - ConfirmedGood:F2}\n" +
                $"确认恢复 {Recovered}   新增不良 {NewBad}（相对原始目标 HBIN；相对基准：恢复 {RuleRecovered} / 新增不良 {RuleNewBad}）\n" +
                $"无法估计 {Unestimable}   覆盖 {Total - Unestimable}/{Total}（{Rate(Total - Unestimable)}）" +
                (Unestimable > 0 ? $"   未覆盖部分取 0～1 时总体范围 {Rate(ExpectedGood)}～{Rate(ExpectedGood + Unestimable)}" : "");
        }
    }
}
