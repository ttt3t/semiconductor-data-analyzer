using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record CsvFormatDetection(AnalyzerConfig Config, IReadOnlyList<string[]> Rows,
    IReadOnlyList<string> Warnings, string Summary);

/// <summary>Local, bounded sampling. Suggestions never change the active configuration.</summary>
public static class CsvFormatDetector
{
    private const int MaxRows = 256, MaxCharacters = 2 * 1024 * 1024;
    private static readonly Dictionary<string, string> Identity = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Definitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
        { "v", "mv", "uv", "nv", "a", "ma", "ua", "na", "pa", "ohm", "kohm", "mohm", "ω", "kω", "mω",
          "hz", "khz", "mhz", "ghz", "s", "ms", "us", "ns", "ps", "f", "pf", "nf", "uf", "%", "db", "dbm", "w", "mw", "c", "degc", "℃", "°c", "none", "unitless", "无单位" };

    static CsvFormatDetector()
    {
        void Add(Dictionary<string, string> map, string key, params string[] aliases)
        { foreach (string alias in aliases) map[Normalize(alias)] = key; }
        Add(Identity, "SN", "SN", "SerialNumber", "SerialNo", "Serial", "PartID", "DeviceID", "DUTID", "ChipID", "序列号", "芯片序列号");
        Add(Identity, "X", "X", "XCoord", "XCoordinate", "DieX", "X坐标");
        Add(Identity, "Y", "Y", "YCoord", "YCoordinate", "DieY", "Y坐标");
        Add(Identity, "Site", "Site", "SiteNum", "SiteNumber", "SiteNo", "测试站点");
        Add(Identity, "Wafer", "Wafer", "WaferID", "WaferNo", "WaferNumber", "片号", "晶圆", "晶圆号");
        Add(Identity, "SBIN", "SBIN", "SoftBin", "SoftwareBin", "SoftBinNumber", "软bin");
        Add(Identity, "HBIN", "HBIN", "HardBin", "HardwareBin", "HardBinNumber", "硬bin");
        Add(Identity, "Product", "Product", "ProductID", "ProductName", "产品", "产品型号");
        Add(Identity, "Program", "Program", "ProgramName", "TestProgram", "JobName", "程序", "测试程序");
        Add(Identity, "Lot", "Lot", "LotID", "LotNo", "LotNumber", "批次", "批次号");
        Add(Definitions, "Name", "TestItem", "TestName", "TestNames", "Parameter", "Parameters", "Name", "SubTest", "测试项", "测试项目", "测试名称", "子项目");
        Add(Definitions, "Low", "LSL", "LowLimit", "LowerLimit", "LowerSpecLimit", "MinLimit", "下限", "规格下限");
        Add(Definitions, "High", "USL", "HighLimit", "UpperLimit", "UpperSpecLimit", "MaxLimit", "上限", "规格上限");
        Add(Definitions, "Unit", "Unit", "Units", "单位");
        Add(Definitions, "Number", "TestNum", "TestNumber", "TestNo", "TestID", "测试序号", "测试编号");
        Add(Definitions, "SBIN", "SBIN", "SoftBin");
        Add(Definitions, "HBIN", "HBIN", "HardBin");
        foreach (string name in new[] { "Result", "PassFail", "PF", "Status", "TestTime", "Timestamp", "StartTime", "EndTime", "Date", "Time", "测试时间", "结果", "判定" })
            Reserved.Add(Normalize(name));
    }

    private static string Normalize(string value) => new(value.Trim().TrimStart('\uFEFF')
        .Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-' and not ':' and not '[' and not ']' and not '(' and not ')' and not '.').ToArray());
    private static string Cell(string[] row, int col) => (uint)col < (uint)row.Length ? row[col].Trim() : "";
    private static string Kind(string[] row)
    {
        string first = row.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        return Definitions.GetValueOrDefault(Normalize(first), "");
    }
    private static bool Numeric(string value) => DatasetService.TryValue(value, out _);

    public static CsvFormatDetection Detect(string path, AnalyzerConfig seed, CancellationToken token = default)
    {
        if (DataFileService.IsStdf(path) || IntegratedCsvService.IsIntegrated(path))
            throw new InvalidDataException("此功能用于普通 CSV；STDF 和软件整合 CSV 已自带格式定义。");
        var warnings = new List<string>();
        string encoding = DetectEncoding(path, warnings);
        var lines = Sample(path, encoding, true, token);
        if (lines.Count < 2) throw new InvalidDataException("样本不足，至少需要表头和一条数据记录。");
        char delimiter = new[] { ',', '\t', ';', '|' }.OrderByDescending(d => DelimiterScore(lines, d)).First();
        var rows = lines.Select(line => CsvParser.ParseLine(line, delimiter, true)).ToList();
        int width = rows.GroupBy(r => r.Length).OrderByDescending(g => g.Count() * Math.Log2(g.Key + 1)).First().Key;
        if (width < 2) throw new InvalidDataException("未识别到稳定的多列 CSV，请在 INI 中手动指定分隔符。");
        int header = Enumerable.Range(0, rows.Count).OrderByDescending(r => HeaderScore(rows[r], width)).First();
        var fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int col = 0; col < rows[header].Length; col++)
            if (Identity.TryGetValue(Normalize(Cell(rows[header], col)), out string? field))
            {
                if (!fields.TryAdd(field, col + 1)) warnings.Add($"身份列 {field} 出现多次，仅建议使用第一列，请核对。");
            }
        int[] reserved = Enumerable.Range(0, rows[header].Length)
            .Where(c => Reserved.Contains(Normalize(Cell(rows[header], c)))).Select(c => c + 1).ToArray();
        var excluded = fields.Values.Concat(reserved).ToHashSet();
        int start = Enumerable.Range(1, width).FirstOrDefault(c => !excluded.Contains(c) && Cell(rows[header], c - 1).Length > 0);
        if (start == 0) throw new InvalidDataException("未找到测量值列，请手动配置测试项的起始列。");
        int[] columns = Enumerable.Range(start, width - start + 1).Where(c => !excluded.Contains(c)).ToArray();
        int[] primary = fields.Where(p => p.Key is not "Product" and not "Program" and not "Lot").Select(p => p.Value - 1).ToArray();
        bool HasIdentity(string[] row) => primary.Any(c => Cell(row, c).Length > 0 &&
            !Identity.ContainsKey(Normalize(Cell(row, c))) && !Definitions.ContainsKey(Normalize(Cell(row, c))));
        int data = -1;
        for (int r = header + 1; r < rows.Count; r++)
        {
            token.ThrowIfCancellationRequested();
            if (Kind(rows[r]).Length > 0) continue;
            // A real chip with invalid text measurements must not be mistaken for a units row.
            if (primary.Length > 0 ? HasIdentity(rows[r]) : !LooksLikeUnits(rows[r], columns) && NumericRatio(rows[r], columns) >= .5)
            { data = r; break; }
        }
        if (data < 0) throw new InvalidDataException("采样范围内未找到数据起始行，请手动指定 INI 布局（最多采样 256 条记录 / 2 MB 文本）。");
        var config = ConfigLoader.Parse(ConfigLoader.Serialize(seed), Path.GetFileNameWithoutExtension(path) + ".auto.ini");
        config.EncodingName = encoding; config.Delimiter = delimiter; config.HasQuotedFields = true;
        config.SerialNumberColumn = fields.GetValueOrDefault("SN");
        config.XColumn = fields.GetValueOrDefault("X"); config.YColumn = fields.GetValueOrDefault("Y");
        config.SiteColumn = fields.GetValueOrDefault("Site"); config.WaferColumn = fields.GetValueOrDefault("Wafer");
        config.SBinColumn = fields.GetValueOrDefault("SBIN"); config.HBinColumn = fields.GetValueOrDefault("HBIN");
        config.ProductColumn = fields.GetValueOrDefault("Product"); config.ProgramColumn = fields.GetValueOrDefault("Program"); config.LotColumn = fields.GetValueOrDefault("Lot");
        config.TestItemStartColumn = start; config.DataStartRow = data + 1;
        config.TestItemColumns = reserved.Any(c => c >= start) ? columns.ToList() : new();
        config.LowLimitRow = config.HighLimitRow = config.UnitRow = config.TestNumberRow = config.SBinRow = config.HBinRow = 0;
        config.SkipRepeatedHeaders = true;

        var names = new SortedSet<int> { header + 1 };
        bool NameLike(int r) => columns.Any(c => Cell(rows[r], c - 1).Length > 0) &&
            columns.Where(c => Cell(rows[r], c - 1).Length > 0).All(c => !Numeric(Cell(rows[r], c - 1))) &&
            !LooksLikeUnits(rows[r], columns) && Kind(rows[r]) is "" or "Name";
        for (int r = header - 1; r >= 0 && NameLike(r) && !HasIdentity(rows[r]); r--) names.Add(r + 1);
        for (int r = header + 1; r < data; r++)
        {
            string kind = Kind(rows[r]);
            switch (kind)
            {
                case "Low": config.LowLimitRow = r + 1; break;
                case "High": config.HighLimitRow = r + 1; break;
                case "Unit": config.UnitRow = r + 1; break;
                case "Number": config.TestNumberRow = r + 1; break;
                case "SBIN": config.SBinRow = r + 1; break;
                case "HBIN": config.HBinRow = r + 1; break;
                default:
                    if (LooksLikeUnits(rows[r], columns)) config.UnitRow = r + 1;
                    else if (NameLike(r)) names.Add(r + 1);
                    break;
            }
        }
        config.TestItemRow = names.Min;
        config.TestItemRows = names.Count > 1 ? names.ToList() : new();
        var unlabeledNumeric = Enumerable.Range(names.Max, Math.Max(0, data - names.Max))
            .Where(r => Kind(rows[r]).Length == 0 && NumericRatio(rows[r], columns) >= .8).ToList();
        if (config.LowLimitRow == 0 && config.HighLimitRow == 0 && unlabeledNumeric.Count >= 2 &&
            OrderedLimits(rows[unlabeledNumeric[0]], rows[unlabeledNumeric[1]], columns))
        {
            config.LowLimitRow = unlabeledNumeric[0] + 1; config.HighLimitRow = unlabeledNumeric[1] + 1;
            unlabeledNumeric.RemoveRange(0, 2);
            warnings.Add("上下限行无标签，按数值关系推测，应用前请核对 LSL / USL。");
        }
        if (config.TestNumberRow == 0 && config.UnitRow > 0)
        {
            int number = unlabeledNumeric.FirstOrDefault(r => r + 1 > config.UnitRow &&
                columns.All(c => long.TryParse(Cell(rows[r], c - 1), out _)) &&
                columns.Select(c => Cell(rows[r], c - 1)).Distinct().Count() == columns.Length, -1);
            if (number >= 0) config.TestNumberRow = number + 1;
        }
        if (config.LowLimitRow == 0 && config.HighLimitRow == 0) warnings.Add("未找到可确认的上下限；不自动从测量值生成规格，请核对原文件。");
        if (config.HBinColumn == 0) warnings.Add("未识别 HBIN 列，原始 HBIN 良率 / 良率预测需要手动补充映射。");
        if (config.SerialNumberColumn == 0) warnings.Add("未识别序列号列，导入时将沿用自动行号编号规则。");
        if (primary.Length == 0) warnings.Add("未识别芯片身份列，无法可靠区分无标签阈值与数据，建议人工确认起始行。");
        if (config.TestItemRows.Count > 1) warnings.Add("已建议多行名称；上层空白向右延续，纵向合并保留本列显式名称，完全空白列不创建测试项。");
        // Validate the exact INI users can save and apply, preserving non-layout settings.
        config = ConfigLoader.Parse(ConfigLoader.Serialize(config), config.ConfigFileName);
        string separator = delimiter == '\t' ? "Tab" : delimiter.ToString();
        string summary = $"编码 {encoding} · 分隔符 {separator} · 名称行 {string.Join(",", CsvText.NameRows(config))} · 数据起始行 {config.DataStartRow} · 测项起始列 {start}";
        return new(config, rows, warnings, summary);
    }

    public static IReadOnlyList<string[]> Preview(string path, AnalyzerConfig config, CancellationToken token = default)
        => Sample(path, config.EncodingName, config.HasQuotedFields, token)
            .Select(line => CsvParser.ParseLine(line, config.Delimiter, config.HasQuotedFields)).ToArray();

    private static List<string> Sample(string path, string encoding, bool quoted, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        using var reader = new StreamReader(file, CsvText.EncodingFor(encoding), true, 65536);
        var rows = new List<string>(); int characters = 0;
        foreach (string row in CsvText.ReadRecords(reader, quoted, token, MaxCharacters))
        {
            token.ThrowIfCancellationRequested();
            if (characters + row.Length > MaxCharacters) break;
            rows.Add(row); characters += row.Length;
            if (rows.Count >= MaxRows) break;
        }
        return rows;
    }

    private static string DetectEncoding(string path, List<string> warnings)
    {
        using var file = File.OpenRead(path);
        byte[] bytes = new byte[Math.Min(file.Length, 65536)]; int count = file.Read(bytes);
        if (count >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) return "utf-32";
        if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff) return "utf-32BE";
        if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return "utf-16";
        if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return "utf-16BE";
        if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) return "UTF-8";
        try
        {
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            decoder.Convert(bytes, new char[count], false, out _, out _, out _);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            warnings.Add("无编码标记且不是有效 UTF-8，暂按 GB18030（兼容 GBK）读取，请核对中文。");
            return "GB18030";
        }
    }

    private static double DelimiterScore(List<string> lines, char delimiter)
    {
        var lengths = lines.Select(l => CsvParser.ParseLine(l, delimiter, true).Length).Where(n => n > 1).ToArray();
        return lengths.Length == 0 ? 0 : lengths.GroupBy(n => n).Max(g => g.Count() * Math.Log2(g.Key + 1));
    }
    private static double HeaderScore(string[] row, int width)
    {
        int fields = row.Select(Normalize).Where(Identity.ContainsKey).Distinct().Count();
        if (Kind(row) is "Low" or "High" or "Unit" or "Number" ||
            (Kind(row) is "SBIN" or "HBIN" && fields < 2)) return -1;
        int text = row.Count(s => !string.IsNullOrWhiteSpace(s) && !Numeric(s));
        return fields * 12 + Math.Log2(text + 1) + (row.Length == width ? 2 : 0);
    }
    private static double NumericRatio(string[] row, int[] columns) =>
        columns.Count(c => Numeric(Cell(row, c - 1))) / (double)Math.Max(1, columns.Length);
    private static bool LooksLikeUnits(string[] row, int[] columns)
    {
        var values = columns.Select(c => Cell(row, c - 1)).Where(s => s.Length > 0).ToArray();
        return values.Length > 0 && values.All(s => Units.Contains(s.Replace('µ', 'u').Replace('μ', 'u')));
    }
    private static bool OrderedLimits(string[] low, string[] high, int[] columns)
    {
        int pairs = 0;
        foreach (int c in columns)
            if (DatasetService.TryValue(Cell(low, c - 1), out double l) && DatasetService.TryValue(Cell(high, c - 1), out double h))
            { if (l > h) return false; pairs++; }
        return pairs > 0;
    }
}
