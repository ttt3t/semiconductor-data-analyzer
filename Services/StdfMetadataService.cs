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
        var info = new FileInfo(path); long length = info.Length; DateTime modified = info.LastWriteTimeUtc;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var reader = new StdfReader(file);
        var builder = new StdfMetadataBuilder();
        while (reader.NextMetadata())
        {
            token.ThrowIfCancellationRequested();
            try { builder.Read(reader.Type, reader.Subtype, reader.Fields); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"STDF 基本信息 {reader.Type}/{reader.Subtype}（截至字节 {reader.Offset}）：{ex.Message}", ex); }
        }
        token.ThrowIfCancellationRequested(); info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("读取 STDF 基本信息期间文件发生变化，请重新导入。");
        return builder.Complete();
    }
}

internal sealed class StdfMetadataBuilder
{
    private sealed class Wafer
    { public string Name = ""; public DateTime? Start, Finish; public readonly List<StdfInformationField> Fields = new(); }
    private readonly List<Wafer> _wafers = new();
    private readonly Dictionary<(byte Head, byte Group), Wafer> _active = new();
    private string _lot = "";
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

    public void Read(byte type, byte subtype, StdfFields f)
    {
        if (_mrr) throw new InvalidDataException("MRR 之后仍有记录，不能读取拼接的数据流。");
        switch ((type, subtype))
        {
            case (0, 10): throw new InvalidDataException("重复 FAR，不能读取拼接的数据流。");
            case (1, 10):
            {
                if (_mir) throw new InvalidDataException("同一数据流包含多个 MIR。");
                Add(_fields, "MIR", "SETUP_T", "设置时间", DateText(Time(f.U4())));
                _start = Time(f.U4()); Add(_fields, "MIR", "START_T", "批次开始时间", DateText(_start));
                Add(_fields, "MIR", "STAT_NUM", "测试站号", f.U1().ToString(CultureInfo.InvariantCulture));
                Add(_fields, "MIR", "MODE_COD", "测试模式代码", CharText(f.U1()));
                Add(_fields, "MIR", "RTST_COD", "复测代码", CharText(f.U1()));
                Add(_fields, "MIR", "PROT_COD", "数据保护代码", CharText(f.U1()));
                ushort burn = f.U2(); Add(_fields, "MIR", "BURN_TIM", "老化时间（分钟）", burn == ushort.MaxValue ? "" : burn.ToString(CultureInfo.InvariantCulture));
                Add(_fields, "MIR", "CMOD_COD", "命令模式代码", CharText(f.U1()));
                foreach (var field in MirStrings) Add(_fields, "MIR", field.Name, field.Description, field.Name == "LOT_ID" ? f.Cn() : f.OptionalCn());
                _lot = _fields.First(v => v.Name == "LOT_ID").Value; _mir = true;
                break;
            }
            case (1, 20):
                _finish = Time(f.U4()); Add(_fields, "MRR", "FINISH_T", "批次结束时间", DateText(_finish));
                Add(_fields, "MRR", "DISP_COD", "批次处置代码", CharText(f.OptionalU1()));
                Add(_fields, "MRR", "USR_DESC", "用户说明", f.OptionalCn());
                Add(_fields, "MRR", "EXC_DESC", "执行软件说明", f.OptionalCn());
                _mrr = true; break;
            case (2, 10):
            {
                byte head = f.U1(), group = f.U1(); var start = Time(f.U4()); string name = (f.OptionalCn() ?? "").Trim();
                var wafer = new Wafer { Name = name, Start = start };
                Add(wafer.Fields, "WIR", "HEAD_NUM", "测试头", head.ToString(CultureInfo.InvariantCulture));
                Add(wafer.Fields, "WIR", "SITE_GRP", "Site Group", group == 255 ? "" : group.ToString(CultureInfo.InvariantCulture));
                Add(wafer.Fields, "WIR", "START_T", "晶圆开始时间", DateText(start));
                Add(wafer.Fields, "WIR", "WAFER_ID", "起始晶圆 ID", name);
                if (!_active.TryAdd((head, group), wafer)) throw new InvalidDataException("同一 Head / Site Group 的 WIR 尚未结束。");
                _wafers.Add(wafer); break;
            }
            case (2, 20):
            {
                byte head = f.U1(), group = f.U1(); var finish = Time(f.U4());
                if (!_active.Remove((head, group), out var wafer)) throw new InvalidDataException("WRR 没有对应 WIR。");
                Add(wafer.Fields, "WRR", "FINISH_T", "晶圆结束时间", DateText(finish));
                Add(wafer.Fields, "WRR", "PART_CNT", "测试数量（文件汇总）", CountText(f.U4()));
                foreach (var field in new[] { ("RTST_CNT", "复测数量"), ("ABRT_CNT", "中止数量"), ("GOOD_CNT", "良品数量"), ("FUNC_CNT", "功能通过数量") })
                    Add(wafer.Fields, "WRR", field.Item1, field.Item2, CountText(f.OptionalU4()));
                string name = (f.OptionalCn() ?? "").Trim();
                Add(wafer.Fields, "WRR", "WAFER_ID", "结束晶圆 ID", name);
                foreach (var field in new[] { ("FABWF_ID", "Fab 晶圆 ID"), ("FRAME_ID", "框架 ID"), ("MASK_ID", "掩模 ID"), ("USR_DESC", "用户说明"), ("EXC_DESC", "执行软件说明") })
                    Add(wafer.Fields, "WRR", field.Item1, field.Item2, f.OptionalCn());
                if (name.Length > 0) wafer.Name = name; // WRR overrides WIR according to STDF V4.
                wafer.Finish = finish; break;
            }
        }
    }
    public StdfFileMetadata Complete()
    {
        if (!_mir || !_mrr || _active.Count > 0) throw new InvalidDataException("STDF 缺少 MIR/MRR 或晶圆尚未结束，基本信息不完整。");
        return new(_lot, _start, _finish, _wafers.Select(w => new StdfWaferMetadata(w.Name, w.Start, w.Finish) { Fields = w.Fields.ToArray() }).ToArray())
            { LotFields = _fields.ToArray() };
    }
}
