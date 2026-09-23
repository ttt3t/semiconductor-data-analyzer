using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class ConfigLoader
{
    public static AnalyzerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到配置文件: {path}");

        return Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path));
    }

    public static AnalyzerConfig Parse(string text, string fileName = "config.ini")
    {
        // An omitted SN mapping is not an implicit first-column mapping.
        var config = new AnalyzerConfig { ConfigFileName = fileName, SerialNumberColumn = 0 };
        var lines = text.Split('\n');

        string? currentSection = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException($"配置行格式应为 参数=值：{line}");

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            Apply(config, currentSection, key, value);
        }

        Validate(config);
        return config;
    }

    private static void Apply(AnalyzerConfig c, string? section, string key, string value)
    {
        if (string.Equals(section, "ProgramCompatibility", StringComparison.OrdinalIgnoreCase))
        {
            if (!c.ProgramCompatibility.TryAdd(key, List(value)))
                throw new InvalidDataException($"程序兼容组名称重复：{key}");
            return;
        }
        if (string.Equals(section, "SBinCategories", StringComparison.OrdinalIgnoreCase))
        {
            var bins = value.Length == 0 ? new List<string>() : value.Split(',', '，').Select(s => s.Trim()).ToList();
            if (bins.Any(string.IsNullOrEmpty)) throw new InvalidDataException($"分类“{key}”的 SBIN 列表中有空项。");
            if (!c.SBinCategories.TryAdd(key, bins)) throw new InvalidDataException($"SBIN 分类名称重复：{key}");
            return;
        }
        switch (key.ToUpperInvariant())
        {
            case "ENCODING": c.EncodingName = value; break;
            case "DELIMITER":
                c.Delimiter = value.Length == 1 ? value[0] :
                              value.Equals("\\t", StringComparison.OrdinalIgnoreCase) ? '\t' :
                              throw new InvalidDataException("Delimiter 必须是单个字符或 \\t");
                break;
            case "HASQUOTEDFIELDS": c.HasQuotedFields = ParseBool(value); break;

            case "TESTITEMROW": c.TestItemRow = ParsePositiveInt(key, value); break;
            case "LOWLIMITROW": c.LowLimitRow = ParseNonNegativeInt(key, value); break;
            case "HIGHLIMITROW": c.HighLimitRow = ParseNonNegativeInt(key, value); break;
            case "UNITROW": c.UnitRow = ParseNonNegativeInt(key, value); break;
            case "TESTNUMBERROW": c.TestNumberRow = ParseNonNegativeInt(key, value); break;
            case "SBINROW": c.SBinRow = ParseNonNegativeInt(key, value); break;
            case "HBINROW": c.HBinRow = ParseNonNegativeInt(key, value); break;
            case "DATASTARTROW": c.DataStartRow = ParsePositiveInt(key, value); break;
            case "TESTITEMSTARTCOLUMN": c.TestItemStartColumn = ParsePositiveInt(key, value); break;

            case "SERIALNUMBERCOLUMN": c.SerialNumberColumn = ParseNonNegativeInt(key, value); break;
            case "XCOLUMN": c.XColumn = ParseNonNegativeInt(key, value); break;
            case "YCOLUMN": c.YColumn = ParseNonNegativeInt(key, value); break;
            case "SITECOLUMN": c.SiteColumn = ParseNonNegativeInt(key, value); break;
            case "WAFERCOLUMN": c.WaferColumn = ParseNonNegativeInt(key, value); break;
            // Accept old files for migration, but never read/filter using their P/F column.
            case "RESULTCOLUMN": case "PASSTEXT": case "FAILTEXT": case "IGNOREFAILEDDEVICES": break;
            case "SBINCOLUMN": c.SBinColumn = ParseNonNegativeInt(key, value); break;
            case "HBINCOLUMN": c.HBinColumn = ParseNonNegativeInt(key, value); break;

            case "PASSHBINS": c.PassHBins = List(value); break;
            case "PARTIALPASSHBINS": c.PartialPassHBins = List(value); break;
            case "PRODUCTCOLUMN": c.ProductColumn = ParseNonNegativeInt(key, value); break;
            case "PROGRAMCOLUMN": c.ProgramColumn = ParseNonNegativeInt(key, value); break;
            case "LOTCOLUMN": c.LotColumn = ParseNonNegativeInt(key, value); break;
            case "PRODUCT": c.Product = value; break;
            case "PROGRAM": c.Program = value; break;
            case "LOT": c.Lot = value; break;
            case "NOTAPPLICABLETOKENS": c.NotApplicableTokens = List(value); break;
            case "MISSINGTOKENS": c.MissingTokens = List(value); break;

            case "BINMODE": c.BinMode = value; break;
            case "BINCOUNT": c.BinCount = ParsePositiveInt(key, value); break;
            case "SHOWLOWLIMIT": c.ShowLowLimit = ParseBool(value); break;
            case "SHOWHIGHLIMIT": c.ShowHighLimit = ParseBool(value); break;
            case "SHOWMEAN": c.ShowMean = ParseBool(value); break;
            case "SHOWTHREESIGMA": c.ShowThreeSigma = ParseBool(value); break;
            case "INCLUDEOUTOFLIMITDATA": c.IncludeOutOfLimitData = ParseBool(value); break;

            case "DECIMALPLACES": c.DecimalPlaces = ParseNonNegativeInt(key, value); break;
            case "MAXTABLEROWS": c.MaxTableRows = ParsePositiveInt(key, value); break;
            case "REMOVEOUTLIERS": c.RemoveOutliers = ParseBool(value); break;
            case "OUTLIERSIGMA":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) ||
                    !double.IsFinite(threshold) || threshold <= 0)
                    throw new InvalidDataException("OutlierSigma 必须是大于 0 的有限数字");
                c.OutlierSigma = threshold;
                break;

            default:
                throw new InvalidDataException($"配置中存在未知参数: {key}");
        }
    }

    private static void Validate(AnalyzerConfig c)
    {
        var pass = c.PassHBins.Select(BinStatisticsService.NormalizeBin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (c.PartialPassHBins.Select(BinStatisticsService.NormalizeBin).Any(pass.Contains))
            throw new InvalidDataException("PassHBins 与 PartialPassHBins 不允许重叠。");
        var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in c.ProgramCompatibility.Values)
            foreach (string program in group)
                if (!programs.Add(program)) throw new InvalidDataException($"程序 {program} 出现在多个兼容组中。");
        if (c.NotApplicableTokens.Intersect(c.MissingTokens, StringComparer.OrdinalIgnoreCase).Any() ||
            c.NotApplicableTokens.Concat(c.MissingTokens).Any(v => DatasetService.TryValue(v, out _)))
            throw new InvalidDataException("不适用与未测标记不能重叠，也不能使用数值作为标记。");
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in c.SBinCategories)
            foreach (string bin in category.Value)
                if (!assigned.Add(BinStatisticsService.NormalizeBin(bin)))
                    throw new InvalidDataException($"SBIN“{bin}”重复归类；每个 SBIN 只能属于一个分类。");
        int maxHeaderRow = Math.Max(c.TestItemRow,
            Math.Max(c.LowLimitRow, Math.Max(c.HighLimitRow,
            Math.Max(c.UnitRow, Math.Max(c.TestNumberRow, Math.Max(c.SBinRow, c.HBinRow))))));

        if (c.DataStartRow <= maxHeaderRow)
            throw new InvalidDataException("DataStartRow 必须大于所有阈值/名称行");

        if (c.TestItemStartColumn < 1)
            throw new InvalidDataException("TestItemStartColumn 必须 ≥ 1");
        if (c.DecimalPlaces > 15)
            throw new InvalidDataException("DecimalPlaces 必须在 0 到 15 之间");
        if (!new[] { "Fixed", "Spec", "SpecZone", "Mean", "MeanCentered", "Median", "MedianCentered" }
            .Contains(c.BinMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("BinMode 必须为 Fixed、SpecZone 或 MedianCentered");
    }

    private static List<string> List(string value) => value.Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static int ParsePositiveInt(string key, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
            throw new InvalidDataException($"{key} 必须是正整数，当前值: {value}");
        return n;
    }

    private static int ParseNonNegativeInt(string key, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            throw new InvalidDataException($"{key} 必须是非负整数，当前值: {value}");
        return n;
    }

    private static bool ParseBool(string value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidDataException($"布尔值必须为 true 或 false，当前值：{value}");
    }

    public static bool CsvDefinitionChanged(AnalyzerConfig a, AnalyzerConfig b) =>
        a.WaferColumn != b.WaferColumn ||
        (a.EncodingName, a.Delimiter, a.HasQuotedFields) != (b.EncodingName, b.Delimiter, b.HasQuotedFields) ||
        (a.TestItemRow, a.LowLimitRow, a.HighLimitRow, a.UnitRow, a.TestNumberRow, a.SBinRow, a.HBinRow,
         a.DataStartRow, a.TestItemStartColumn) !=
        (b.TestItemRow, b.LowLimitRow, b.HighLimitRow, b.UnitRow, b.TestNumberRow, b.SBinRow, b.HBinRow,
         b.DataStartRow, b.TestItemStartColumn) ||
        (a.SerialNumberColumn, a.XColumn, a.YColumn, a.SiteColumn, a.SBinColumn, a.HBinColumn) !=
        (b.SerialNumberColumn, b.XColumn, b.YColumn, b.SiteColumn, b.SBinColumn, b.HBinColumn) ||
        (a.ProductColumn, a.ProgramColumn, a.LotColumn, a.Product, a.Program, a.Lot) !=
        (b.ProductColumn, b.ProgramColumn, b.LotColumn, b.Product, b.Program, b.Lot);

    public static string Serialize(AnalyzerConfig c, bool includeRobust = true)
    {
        string text = FormattableString.Invariant($"""
            [File]
            Encoding={c.EncodingName}
            Delimiter={(c.Delimiter == '\t' ? "\\t" : c.Delimiter.ToString())}
            HasQuotedFields={c.HasQuotedFields}

            [Layout]
            TestItemRow={c.TestItemRow}
            LowLimitRow={c.LowLimitRow}
            HighLimitRow={c.HighLimitRow}
            UnitRow={c.UnitRow}
            TestNumberRow={c.TestNumberRow}
            SBinRow={c.SBinRow}
            HBinRow={c.HBinRow}
            DataStartRow={c.DataStartRow}
            TestItemStartColumn={c.TestItemStartColumn}

            [IdentityColumns]
            SerialNumberColumn={c.SerialNumberColumn}
            XColumn={c.XColumn}
            YColumn={c.YColumn}
            SiteColumn={c.SiteColumn}
            WaferColumn={c.WaferColumn}
            SBinColumn={c.SBinColumn}
            HBinColumn={c.HBinColumn}

            ProductColumn={c.ProductColumn}
            ProgramColumn={c.ProgramColumn}
            LotColumn={c.LotColumn}

            [HBINClassification]
            PassHBins={string.Join(",", c.PassHBins)}
            PartialPassHBins={string.Join(",", c.PartialPassHBins)}

            [PredictionContext]
            ; 列号为 0 或单元格为空时使用下列默认值；产品/程序未知时不做缺测预测。
            Product={c.Product}
            Program={c.Program}
            Lot={c.Lot}
            NotApplicableTokens={string.Join(",", c.NotApplicableTokens)}
            MissingTokens={string.Join(",", c.MissingTokens)}

            [Histogram]
            BinMode={c.BinMode}
            BinCount={c.BinCount}
            ShowLowLimit={c.ShowLowLimit}
            ShowHighLimit={c.ShowHighLimit}
            ShowMean={c.ShowMean}
            ShowThreeSigma={c.ShowThreeSigma}
            IncludeOutOfLimitData={c.IncludeOutOfLimitData}

            [Display]
            DecimalPlaces={c.DecimalPlaces}
            MaxTableRows={c.MaxTableRows}
            """);
        if (includeRobust)
            text += Environment.NewLine + Environment.NewLine + FormattableString.Invariant($"""
                [Robust]
                RemoveOutliers={c.RemoveOutliers}
                OutlierSigma={c.OutlierSigma:R}
                """);
        text += Environment.NewLine + Environment.NewLine + "[SBinCategories]" + Environment.NewLine
            + "; 每行：分类名称=SBIN1,SBIN2；未配置的 SBIN 显示为未分类。" + Environment.NewLine
            + "; 取消以下示例行开头的分号即可启用。" + Environment.NewLine
            + "; 坏点=1,2,3" + Environment.NewLine + "; 坏线=4,5,6" + Environment.NewLine;
        foreach (var category in c.SBinCategories)
            text += category.Key + "=" + string.Join(",", category.Value) + Environment.NewLine;
        text += Environment.NewLine + "[ProgramCompatibility]" + Environment.NewLine
            + "; 可选：经确认兼容的程序组=程序A,程序B；默认只使用相同程序。" + Environment.NewLine;
        foreach (var group in c.ProgramCompatibility)
            text += group.Key + "=" + string.Join(",", group.Value) + Environment.NewLine;
        return text;
    }

    public static void Save(string path, AnalyzerConfig config)
    {
        string text = Serialize(config);
        Parse(text); // Validate exactly what will be read back before replacing a file.
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
