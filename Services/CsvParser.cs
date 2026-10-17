using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed partial class CsvParseResult
{
    public string SourceDescription { get; set; } = "";
    public StdfFileMetadata? StdfMetadata { get; set; }
    public List<TestItemData> Items { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<CsvRecord> Records { get; set; } = new();
    private List<CsvRecord>? _wafersSource;
    private int _wafersRecordCount = -1;
    private IReadOnlyList<string>? _wafersCache;
    public IReadOnlyList<string> Wafers
    {
        get
        {
            if (_wafersCache == null || !ReferenceEquals(_wafersSource, Records) || _wafersRecordCount != Records.Count)
            {
                _wafersSource = Records;
                _wafersRecordCount = Records.Count;
                _wafersCache = Records.Select(r => r.WaferId).Distinct().ToArray();
            }
            return _wafersCache;
        }
    }
}

public static class CsvParser
{
    public static CsvParseResult Parse(string csvPath, AnalyzerConfig config, Action<string>? progress = null)
        => Parse(csvPath, config, progress, default);

    public static CsvParseResult Parse(string csvPath, AnalyzerConfig config, Action<string>? progress, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (IntegratedCsvService.IsIntegrated(csvPath))
        {
            return IntegratedCsvService.Load(csvPath);
        }
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(config.EncodingName); }
        catch { encoding = Encoding.UTF8; }

        FileStream stream;
        try
        {
            stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        }
        catch (IOException ex) when (ex.Message.Contains("being used by another process"))
        {
            throw new IOException("文件正在被 Excel 或其他程序占用，请先关闭后再试。", ex);
        }

        using var input = stream;
        // Keep only definition rows. A first pass preserves validation against the
        // widest row without retaining the whole file and a second split-string copy.
        var headerRows = new HashSet<int>(new[] { config.TestItemRow, config.LowLimitRow,
            config.HighLimitRow, config.UnitRow, config.TestNumberRow, config.SBinRow, config.HBinRow });
        var rows = new Dictionary<int, string[]>();
        int rowCount = 0, maxCols = 0;
        using (var reader = new StreamReader(input, encoding, true, 65536, leaveOpen: true))
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowCount++;
                maxCols = Math.Max(maxCols, CountColumns(line, config.Delimiter, config.HasQuotedFields));
                if (headerRows.Contains(rowCount)) rows[rowCount - 1] = ParseLine(line, config.Delimiter, config.HasQuotedFields);
            }
        if (rowCount < config.DataStartRow)
            throw new InvalidDataException($"CSV 行数不足，需要至少 {config.DataStartRow} 行");
        if (config.WaferColumn > maxCols)
            throw new InvalidDataException($"WaferColumn ({config.WaferColumn}) 超出 CSV 实际列数 ({maxCols})");
        if (new[] { config.ProductColumn, config.ProgramColumn, config.LotColumn, config.HBinColumn }.Any(c => c > maxCols))
            throw new InvalidDataException("产品 / 程序 / 批次 / HBIN 的配置列号超出 CSV 实际列数。");
        if (maxCols < config.TestItemStartColumn)
            throw new InvalidDataException($"CSV 实际列数 ({maxCols}) 小于配置的 TestItemStartColumn ({config.TestItemStartColumn})");

        var testItemRow = GetRow(rows, config.TestItemRow - 1);
        var lowRow = config.LowLimitRow > 0 ? GetRow(rows, config.LowLimitRow - 1) : null;
        var highRow = config.HighLimitRow > 0 ? GetRow(rows, config.HighLimitRow - 1) : null;
        var unitRow = config.UnitRow > 0 ? GetRow(rows, config.UnitRow - 1) : null;
        var testNumberRow = config.TestNumberRow > 0 ? GetRow(rows, config.TestNumberRow - 1) : null;
        var sbinHeaderRow = config.SBinRow > 0 ? GetRow(rows, config.SBinRow - 1) : null;
        var hbinHeaderRow = config.HBinRow > 0 ? GetRow(rows, config.HBinRow - 1) : null;

        var items = new List<TestItemData>();
        var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int col = config.TestItemStartColumn - 1; col < maxCols; col++)
        {
            string name = GetCell(testItemRow, col).Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!nameCount.TryGetValue(name, out _))
                nameCount[name] = 0;
            nameCount[name]++;
            string displayName = nameCount[name] > 1 ? $"{col + 1}_{name}" : name;

            items.Add(new TestItemData
            {
                ColumnIndex = col,
                Name = name,
                DisplayName = displayName,
                TestNumber = GetCell(testNumberRow, col).Trim(),
                SBin = GetCell(sbinHeaderRow, col).Trim(),
                HBin = GetCell(hbinHeaderRow, col).Trim(),
                Unit = GetCell(unitRow, col).Trim(),
                LowLimit = ParseDoubleOrNull(GetCell(lowRow, col)),
                HighLimit = ParseDoubleOrNull(GetCell(highRow, col))
            });
        }

        if (items.Count == 0)
            throw new InvalidDataException("未解析到任何有效测试项");

        var records = new List<CsvRecord>(Math.Max(0, rowCount - config.DataStartRow + 1));
        var schema = new MeasurementSchema(items.Select(i => i.Id));
        string waferId = Path.GetFileNameWithoutExtension(csvPath);
        string unannotatedWaferId = $"{waferId} (未标注 wafer)";
        var rawStore = new RawMeasurementStore();
        try
        {
            progress?.Invoke("正在读取数据行...");
            input.Position = 0;
            using var dataReader = new StreamReader(input, encoding, true, 65536, leaveOpen: true);
            var rowCells = new List<string>(maxCols);
            var quoteBuffer = new StringBuilder(64);
            var rawMeasurements = new string?[items.Count];
            int r = 0;
            while (dataReader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++r < config.DataStartRow) continue;
                ParseLineInto(line, config.Delimiter, config.HasQuotedFields, rowCells, quoteBuffer);

                string sn = GetCell(rowCells, config.SerialNumberColumn - 1);
                string site = config.SiteColumn > 0 ? GetCell(rowCells, config.SiteColumn - 1) : "";
                int? x = config.XColumn > 0 ? ParseIntOrNull(GetCell(rowCells, config.XColumn - 1)) : null;
                int? y = config.YColumn > 0 ? ParseIntOrNull(GetCell(rowCells, config.YColumn - 1)) : null;
                string sbin = config.SBinColumn > 0 ? GetCell(rowCells, config.SBinColumn - 1).Trim() : "";
                string hbin = config.HBinColumn > 0 ? GetCell(rowCells, config.HBinColumn - 1).Trim() : "";

                string Context(int column, string fallback)
                {
                    if (column <= 0) return fallback;
                    string cell = GetCell(rowCells, column - 1);
                    return string.IsNullOrWhiteSpace(cell) ? fallback : cell.Trim();
                }

                for (int i = 0; i < items.Count; i++)
                    rawMeasurements[i] = GetCell(rowCells, items[i].ColumnIndex);

                string rowWafer;
                if (config.WaferColumn == 0) rowWafer = waferId;
                else
                {
                    string wCell = GetCell(rowCells, config.WaferColumn - 1);
                    rowWafer = string.IsNullOrWhiteSpace(wCell) ? unannotatedWaferId : wCell.Trim();
                }

                var record = new CsvRecord
                {
                    WaferId = rowWafer,
                    SerialNumber = sn,
                    X = x,
                    Y = y,
                    Site = site,
                    Product = Context(config.ProductColumn, config.Product),
                    Program = Context(config.ProgramColumn, config.Program),
                    Lot = Context(config.LotColumn, config.Lot),
                    SBin = sbin,
                    HBin = hbin,
                    Measurements = rawStore.Append(schema, rawMeasurements)
                };
                records.Add(record);
            }

            rawStore.Seal();
            var parsed = DatasetService.Build(items, records);
            progress?.Invoke($"解析完成，共 {items.Count} 个测试项，{parsed.Devices.Count} 颗唯一芯片");
            return parsed;
        }
        catch { rawStore.Dispose(); throw; }
    }

    private static string[] GetRow(Dictionary<int, string[]> rows, int index)
    {
        if (!rows.TryGetValue(index, out var row))
            throw new InvalidDataException($"配置的行号超出 CSV 实际行数 (需要第 {index + 1} 行)");
        return row;
    }

    private static int CountColumns(string line, char delimiter, bool hasQuoted)
    {
        int count = 1;
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (hasQuoted && line[i] == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') i++;
                else inQuotes = !inQuotes;
            }
            else if (line[i] == delimiter && !inQuotes) count++;
        }
        return count;
    }

    private static string GetCell(string[]? row, int col)
    {
        if (row == null || col < 0 || col >= row.Length) return "";
        return row[col];
    }

    private static string GetCell(List<string> row, int col)
    {
        if ((uint)col >= (uint)row.Count) return "";
        return row[col];
    }

    private static string[] ParseLine(string line, char delimiter, bool hasQuoted)
    {
        if (!hasQuoted)
            return line.Split(delimiter);

        var result = new List<string>();
        var sb = new StringBuilder();
        ParseLineInto(line, delimiter, true, result, sb);
        return result.ToArray();
    }

    private static void ParseLineInto(string line, char delimiter, bool hasQuoted, List<string> result, StringBuilder sb)
    {
        result.Clear();
        if (!hasQuoted)
        {
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == delimiter)
                {
                    int len = i - start;
                    result.Add(len == 0 ? "" : line.Substring(start, len));
                    start = i + 1;
                }
            }
            int tailLen = line.Length - start;
            result.Add(tailLen == 0 ? "" : line.Substring(start, tailLen));
            return;
        }

        sb.Clear();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(sb.Length == 0 ? "" : sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.Length == 0 ? "" : sb.ToString());
    }

    private static bool TryParseDouble(string s, out double value)
    {
        return DatasetService.TryValue(s, out value);
    }

    private static double? ParseDoubleOrNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return TryParseDouble(s, out double v) ? v : null;
    }

    private static int? ParseIntOrNull(string s)
    {
        if (int.TryParse(s.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return v;
        return null;
    }
}
