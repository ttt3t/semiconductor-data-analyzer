using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

internal enum LayoutSelection { Row, Rows, Column, Columns }

internal sealed record LayoutField(string Key, string Label, LayoutSelection Selection, Func<AnalyzerConfig, object> Read,
    bool Optional = false, string Section = "Layout")
{
    public bool IsRow => Selection is LayoutSelection.Row or LayoutSelection.Rows;
    public bool IsMultiple => Selection is LayoutSelection.Rows or LayoutSelection.Columns;
    public string Tooltip => Selection == LayoutSelection.Rows ? "TestItemRow / TestItemRows" : Key;

    public string Value(AnalyzerConfig config)
    {
        var value = Read(config);
        return value is IEnumerable<int> list ? string.Join(",", list) : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    public int[] ReadPositions(string input)
    {
        input = input.Trim();
        if (Optional && input.Length == 0) return IsMultiple ? Array.Empty<int>() : new[] { 0 };
        var parts = IsMultiple ? input.Split(',', '，') : new[] { input };
        var positions = new SortedSet<int>();
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position) ||
                position < (Optional && !IsMultiple ? 0 : 1))
                throw new InvalidDataException($"{Label}需要{(IsMultiple ? "用逗号分隔的正整数" : Optional ? "非负整数（0 表示无）" : "正整数")}。");
            positions.Add(position);
        }
        return positions.ToArray();
    }
}

internal static class IniLayoutEditor
{
    private const string Identity = "IdentityColumns";
    internal static readonly LayoutField[] Fields =
    {
        new("TestItemRows", "测项名称行", LayoutSelection.Rows, c => CsvText.NameRows(c)),
        new("TestNumberRow", "测试编号行", LayoutSelection.Row, c => c.TestNumberRow, true),
        new("UnitRow", "单位行", LayoutSelection.Row, c => c.UnitRow, true),
        new("LowLimitRow", "下限行 (LSL)", LayoutSelection.Row, c => c.LowLimitRow, true),
        new("HighLimitRow", "上限行 (USL)", LayoutSelection.Row, c => c.HighLimitRow, true),
        new("SBinRow", "测项 SBIN 行", LayoutSelection.Row, c => c.SBinRow, true),
        new("HBinRow", "测项 HBIN 行", LayoutSelection.Row, c => c.HBinRow, true),
        new("DataStartRow", "数据起始行", LayoutSelection.Row, c => c.DataStartRow),
        new("TestItemStartColumn", "测项起始列", LayoutSelection.Column, c => c.TestItemStartColumn),
        new("TestItemColumns", "指定测项列", LayoutSelection.Columns, c => c.TestItemColumns, true),
        new("SerialNumberColumn", "芯片序列号 (SN)", LayoutSelection.Column, c => c.SerialNumberColumn, true, Identity),
        new("XColumn", "X 坐标", LayoutSelection.Column, c => c.XColumn, true, Identity),
        new("YColumn", "Y 坐标", LayoutSelection.Column, c => c.YColumn, true, Identity),
        new("SiteColumn", "测试站点 (Site)", LayoutSelection.Column, c => c.SiteColumn, true, Identity),
        new("WaferColumn", "晶圆号 (Wafer)", LayoutSelection.Column, c => c.WaferColumn, true, Identity),
        new("SBinColumn", "记录 SBIN", LayoutSelection.Column, c => c.SBinColumn, true, Identity),
        new("HBinColumn", "记录 HBIN", LayoutSelection.Column, c => c.HBinColumn, true, Identity),
        new("ProductColumn", "产品", LayoutSelection.Column, c => c.ProductColumn, true, Identity),
        new("ProgramColumn", "测试程序", LayoutSelection.Column, c => c.ProgramColumn, true, Identity),
        new("LotColumn", "批次 (Lot)", LayoutSelection.Column, c => c.LotColumn, true, Identity)
    };

    internal static string Apply(string original, AnalyzerConfig baseline, IReadOnlyDictionary<LayoutField, int[]> positions,
        bool skipRepeatedHeaders)
    {
        var changes = new List<(string Section, string Key, string Value)>();
        foreach (var (field, indices) in positions)
        {
            string value = string.Join(",", indices);
            if (value == field.Value(baseline)) continue;
            if (field.Selection == LayoutSelection.Rows)
            {
                changes.Add((field.Section, "TestItemRow", indices[0].ToString(CultureInfo.InvariantCulture)));
                value = indices.Length > 1 ? value : "";
            }
            changes.Add((field.Section, field.Key, value));
        }
        if (skipRepeatedHeaders != baseline.SkipRepeatedHeaders)
            changes.Add(("Layout", "SkipRepeatedHeaders", skipRepeatedHeaders ? "true" : "false"));
        if (changes.Count == 0) return original;

        string ending = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        foreach (var (section, key, value) in changes)
            PatchValue(lines, section, key, value);
        string text = string.Join(ending, lines);
        _ = ConfigLoader.Parse(text, baseline.ConfigFileName);
        return text;
    }

    private static void PatchValue(List<string> lines, string section, string key, string value)
    {
        int index = -1, sectionEnd = -1;
        string current = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionEnd = i + 1;
                continue;
            }
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && line.Length > 0)
                sectionEnd = i + 1;
            if (line.Length == 0 || line[0] is ';' or '#' ||
                current.Equals("SBinDescriptions", StringComparison.OrdinalIgnoreCase) ||
                current.Equals("SBinCategories", StringComparison.OrdinalIgnoreCase) ||
                current.Equals("ProgramCompatibility", StringComparison.OrdinalIgnoreCase)) continue;
            int equals = line.IndexOf('=');
            // The last occurrence also wins in legacy files with keys outside a section.
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                index = i;
        }
        if (index >= 0)
        {
            string line = lines[index];
            int start = line.IndexOf('=') + 1;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            int end = Math.Max(start, line.TrimEnd().Length);
            lines[index] = line[..start] + value + line[end..];
            return;
        }
        if (sectionEnd >= 0)
        {
            lines.Insert(sectionEnd, key + "=" + value);
            return;
        }
        int insertion = lines.Count;
        while (insertion > 0 && lines[insertion - 1].Length == 0) insertion--;
        if (insertion > 0) lines.Insert(insertion++, "");
        lines.Insert(insertion++, "[" + section + "]");
        lines.Insert(insertion, key + "=" + value);
    }
}
