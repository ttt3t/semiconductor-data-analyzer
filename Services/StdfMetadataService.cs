using System.IO;
using System.Globalization;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record StdfInformationField(string Record, string Name, string Description, string Value);

public sealed record StdfWaferMetadata(string WaferId, DateTime? StartTime, DateTime? FinishTime)
{
    public IReadOnlyList<StdfInformationField> Fields { get; init; } = Array.Empty<StdfInformationField>();
}

public sealed record StdfFileMetadata(string LotId, DateTime? StartTime, DateTime? FinishTime, IReadOnlyList<StdfWaferMetadata> Wafers)
{
    public IReadOnlyList<StdfInformationField> LotFields { get; init; } = Array.Empty<StdfInformationField>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string[] WaferIds => Wafers.Select(w => w.WaferId).Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    public DateTime? TestTime => Wafers.Count == 1 ? Wafers[0].StartTime ?? StartTime ?? Wafers[0].FinishTime ?? FinishTime :
        StartTime ?? Wafers.Min(w => w.StartTime) ?? FinishTime ?? Wafers.Min(w => w.FinishTime);
    public DateTime? TimeForWafer(string wafer)
    {
        var matches = Wafers.Where(w => w.WaferId == wafer).ToArray();
        return matches.Length == 0 ? Wafers.Count == 1 ? TestTime : StartTime ?? FinishTime :
            matches.Min(w => w.StartTime) ?? StartTime ?? matches.Min(w => w.FinishTime) ?? FinishTime;
    }
}

/// <summary>Reads only MIR/WIR/WRR/MRR fields, never creates measurements or numeric columns.</summary>
public static class StdfMetadataService
{
    public static StdfFileMetadata Read(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var file = SourceFileReader.Open(path);
        var stamp = new SourceFileStamp(file);
        var builder = new StdfMetadataBuilder();
        using var reader = new StdfReader(file, builder.Warnings);
        while (reader.NextMetadata())
        {
            token.ThrowIfCancellationRequested();
            builder.Read(reader.Type, reader.Subtype, reader.Fields, reader.Offset);
        }
        token.ThrowIfCancellationRequested(); stamp.Validate(file);
        return builder.Complete();
    }
}

internal sealed class StdfMetadataBuilder
{
    public StdfWarnings Warnings { get; }
    public StdfMetadataBuilder(StdfWarnings? warnings = null) => Warnings = warnings ?? new();
    private sealed class Wafer
    { public string Name = ""; public DateTime? Start, Finish; public readonly List<StdfInformationField> Fields = new(); }
    private readonly List<Wafer> _wafers = new();
    private readonly Dictionary<(byte Head, byte Group), Wafer> _active = new();
    private string _lot = "";
    private readonly HashSet<string> _lots = new(StringComparer.Ordinal);
    private DateTime? _start, _finish;
    private bool _mir, _mrr;
    private readonly List<StdfInformationField> _fields = new();
    private static readonly (string Name, string Description)[] MirStrings =
    {
        ("LOT_ID", "批次"), ("PART_TYP", "产品型号"), ("NODE_NAM", "机台名称"), ("TSTR_TYP", "测试机型号"),
        ("JOB_NAM", "测试程序"), ("JOB_REV", "程序版本"), ("SBLOT_ID", "子批次"), ("OPER_NAM", "操作员"),
        ("EXEC_TYP", "执行软件"), ("EXEC_VER", "软件版本"), ("TEST_COD", "测试阶段"), ("TST_TEMP", "测试温度（原文）"),
        ("USER_TXT", "用户备注"), ("AUX_FILE", "辅助文件"), ("PKG_TYP", "封装"), ("FAMLY_ID", "产品系列"),
        ("DATE_COD", "日期代码"), ("FACIL_ID", "测试工厂"), ("FLOOR_ID", "测试楼层"), ("PROC_ID", "工艺"),
        ("OPER_FRQ", "工作频率 / 步骤"), ("SPEC_NAM", "测试规范"), ("SPEC_VER", "规范版本"), ("FLOW_ID", "Test Flow"),
        ("SETUP_ID", "测试设置"), ("DSGN_REV", "设计版本"), ("ENG_ID", "工程批次"), ("ROM_COD", "ROM 代码"),
        ("SERL_NUM", "测试机序列号"), ("SUPR_NAM", "主管")
    };
    private static string DateText(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    private static string CharText(byte? value) => value is null or 0 or 32 ? "" : ((char)value.Value).ToString();
    private static string CountText(uint? value) => value is null or uint.MaxValue ? "" : value.Value.ToString(CultureInfo.InvariantCulture);
    private static void Add(List<StdfInformationField> fields, string record, string name, string description, string? value)
        => fields.Add(new(record, name, description, value?.Trim() ?? ""));
    public DateTime? LotStart => _start;
    public DateTime? LotFinish => _finish;
    internal static DateTime? Time(uint value) => value is 0 or uint.MaxValue ? null :
        DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime().DateTime;

    public void Read(byte type, byte subtype, StdfFields f, long offset = 0)
    {
        try { ReadCore(type, subtype, f); }
        catch (InvalidDataException ex)
        { Warnings.Add($"metadata:{type}/{subtype}:{ex.Message}", $"基本信息 {type}/{subtype}（截至字节 {offset}）不完整，已保留可读字段：{ex.Message}"); }
    }

    private void ReadCore(byte type, byte subtype, StdfFields f)
    {
        switch ((type, subtype))
        {
            case (0, 10): Warnings.Add("repeated-far", "文件包含重复 FAR，继续读取兼容编码的记录。"); break;
            case (1, 10):
            {
                if (_mir) Warnings.Add("repeated-mir", "文件包含多个 MIR，已保留各段可读信息，请核对批次归属。");
                _mir = true;
                Add(_fields, "MIR", "SETUP_T", "设置时间", DateText(Time(f.U4())));
                _start = Time(f.U4()); Add(_fields, "MIR", "START_T", "批次开始时间", DateText(_start));
                Add(_fields, "MIR", "STAT_NUM", "测试站号", f.U1().ToString(CultureInfo.InvariantCulture));
                Add(_fields, "MIR", "MODE_COD", "测试模式代码", CharText(f.U1()));
                Add(_fields, "MIR", "RTST_COD", "复测代码", CharText(f.U1()));
                Add(_fields, "MIR", "PROT_COD", "数据保护代码", CharText(f.U1()));
                ushort burn = f.U2(); Add(_fields, "MIR", "BURN_TIM", "老化时间（分钟）", burn == ushort.MaxValue ? "" : burn.ToString(CultureInfo.InvariantCulture));
                Add(_fields, "MIR", "CMOD_COD", "命令模式代码", CharText(f.U1()));
                foreach (var field in MirStrings)
                {
                    string value = f.OptionalCn() ?? "";
                    Add(_fields, "MIR", field.Name, field.Description, value);
                    if (field.Name == "LOT_ID")
                    {
                        _lot = value.Trim();
                        if (_lot.Length > 0) _lots.Add(_lot);
                    }
                }
                break;
            }
            case (1, 20):
                _mrr = true;
                _finish = Time(f.U4()); Add(_fields, "MRR", "FINISH_T", "批次结束时间", DateText(_finish));
                Add(_fields, "MRR", "DISP_COD", "批次处置代码", CharText(f.OptionalU1()));
                Add(_fields, "MRR", "USR_DESC", "用户说明", f.OptionalCn());
                Add(_fields, "MRR", "EXC_DESC", "执行软件说明", f.OptionalCn());
                break;
            case (2, 10):
            {
                byte head = f.U1(), group = f.U1(); var start = Time(f.U4()); string name = (f.OptionalCn() ?? "").Trim();
                var wafer = new Wafer { Name = name, Start = start };
                Add(wafer.Fields, "WIR", "HEAD_NUM", "测试头", head.ToString(CultureInfo.InvariantCulture));
                Add(wafer.Fields, "WIR", "SITE_GRP", "Site Group", group == 255 ? "" : group.ToString(CultureInfo.InvariantCulture));
                Add(wafer.Fields, "WIR", "START_T", "晶圆开始时间", DateText(start));
                Add(wafer.Fields, "WIR", "WAFER_ID", "起始晶圆 ID", name);
                if (_active.ContainsKey((head, group))) Warnings.Add("metadata:missing-wrr", "WIR 尚未对应 WRR 便开始下一片，已保留两片基本信息。");
                _active[(head, group)] = wafer;
                _wafers.Add(wafer); break;
            }
            case (2, 20):
            {
                byte head = f.U1(), group = f.U1(); var finish = Time(f.U4());
                if (!_active.Remove((head, group), out var wafer))
                {
                    Warnings.Add("metadata:missing-wir", "WRR 没有对应 WIR，按 WRR 恢复晶圆基本信息。");
                    wafer = new Wafer(); _wafers.Add(wafer);
                }
                wafer.Finish = finish;
                Add(wafer.Fields, "WRR", "FINISH_T", "晶圆结束时间", DateText(finish));
                Add(wafer.Fields, "WRR", "PART_CNT", "测试数量（文件汇总）", CountText(f.U4()));
                foreach (var field in new[] { ("RTST_CNT", "复测数量"), ("ABRT_CNT", "中止数量"), ("GOOD_CNT", "良品数量"), ("FUNC_CNT", "功能通过数量") })
                    Add(wafer.Fields, "WRR", field.Item1, field.Item2, CountText(f.OptionalU4()));
                string name = (f.OptionalCn() ?? "").Trim();
                Add(wafer.Fields, "WRR", "WAFER_ID", "结束晶圆 ID", name);
                foreach (var field in new[] { ("FABWF_ID", "Fab 晶圆 ID"), ("FRAME_ID", "框架 ID"), ("MASK_ID", "掩模 ID"), ("USR_DESC", "用户说明"), ("EXC_DESC", "执行软件说明") })
                    Add(wafer.Fields, "WRR", field.Item1, field.Item2, f.OptionalCn());
                if (name.Length > 0) wafer.Name = name; // WRR overrides WIR according to STDF V4.
                break;
            }
        }
    }
    public StdfFileMetadata Complete()
    {
        if (!_mir) Warnings.Add("missing-mir", "缺少 MIR，批次、产品、程序或开始时间可能缺失；缺失项沿用配置或登记值。");
        if (!_mrr) Warnings.Add("missing-mrr", "缺少 MRR，结束时间或汇总信息可能缺失，已保留可读数据。");
        if (_active.Count > 0) Warnings.Add("metadata:open-wafer", $"有 {_active.Count} 个晶圆缺少 WRR，已保留 WIR 信息。");
        if (_lots.Count > 1) Warnings.Add("multiple-lots", "文件含多个批次，未使用单一 Lot 自动合并晶圆，请核对登记批次。");
        return new(_lots.Count > 1 ? "" : _lot, _start, _finish, _wafers.Select(w => new StdfWaferMetadata(w.Name, w.Start, w.Finish) { Fields = w.Fields.ToArray() }).ToArray())
            { LotFields = _fields.ToArray(), Warnings = Warnings.Messages };
    }
}
