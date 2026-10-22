using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed partial class CsvParseResult
{
    internal bool IsYieldProjection { get; set; }
    public string SourceDescription { get; set; } = "";
    public StdfFileMetadata? StdfMetadata { get; set; }
    public List<TestItemData> Items { get; set; } = new();
    public IReadOnlyList<DeviceInfo> Devices { get; set; } = Array.Empty<DeviceInfo>();
    public RecordList Records { get; set; } = new();
    private RecordList? _wafersSource;
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
    internal void SetKnownWafers(IReadOnlyList<string> wafers)
    { _wafersSource = Records; _wafersRecordCount = Records.Count; _wafersCache = wafers; }
}

public static class CsvParser
{
    public static CsvParseResult Parse(string csvPath, AnalyzerConfig config, Action<string>? progress = null)
        => Parse(csvPath, config, progress, default);

    public static CsvParseResult Parse(string csvPath, AnalyzerConfig config, Action<string>? progress, CancellationToken cancellationToken)
        => ParseCore(csvPath, config, progress, cancellationToken, false);

    internal static CsvParseResult ParseCore(string csvPath, AnalyzerConfig config, Action<string>? progress,
        CancellationToken cancellationToken, bool yieldOnly, bool deferColumns = false)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (IntegratedCsvService.IsIntegrated(csvPath))
        {
            return IntegratedCsvService.LoadCore(csvPath, yieldOnly ? config : null);
        }
        Encoding encoding;
        try { encoding = CsvText.EncodingFor(config.EncodingName); }
        catch { encoding = Encoding.UTF8; }

        using var input = SourceFileReader.Open(csvPath);
        var stamp = new SourceFileStamp(input);
        // Keep only definition rows. A first pass preserves validation against the
        // widest row without retaining the whole file and a second split-string copy.
        var headerRows = new HashSet<int>(new[] { config.TestItemRow, config.LowLimitRow,
            config.HighLimitRow, config.UnitRow, config.TestNumberRow, config.SBinRow, config.HBinRow });
        var nameRows = CsvText.NameRows(config);
        headerRows.UnionWith(nameRows);
        var definitionSignatures = new List<ulong>();
        var rows = new Dictionary<int, string[]>();
        int rowCount = 0, maxCols = 0;
        using (var reader = new StreamReader(input, encoding, true, 65536, leaveOpen: true))
            foreach (string line in CsvText.ReadRecords(reader, config.HasQuotedFields, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowCount++;
                maxCols = Math.Max(maxCols, CountColumns(line, config.Delimiter, config.HasQuotedFields));
                if (headerRows.Contains(rowCount) || (config.SkipRepeatedHeaders && rowCount < config.DataStartRow))
                {
                    var cells = ParseLine(line, config.Delimiter, config.HasQuotedFields);
                    if (headerRows.Contains(rowCount)) rows[rowCount - 1] = cells;
                    if (rowCount < config.DataStartRow) definitionSignatures.Add(CsvText.Signature(cells));
                }
            }
        stamp.Validate(input);
        if (rowCount < config.DataStartRow)
            throw new InvalidDataException($"CSV 行数不足，需要至少 {config.DataStartRow} 行");
        if (config.WaferColumn > maxCols)
            throw new InvalidDataException($"WaferColumn ({config.WaferColumn}) 超出 CSV 实际列数 ({maxCols})");
        if (new[] { config.ProductColumn, config.ProgramColumn, config.LotColumn, config.HBinColumn }.Any(c => c > maxCols))
            throw new InvalidDataException("产品 / 程序 / 批次 / HBIN 的配置列号超出 CSV 实际列数。");
        if (maxCols < config.TestItemStartColumn)
            throw new InvalidDataException($"CSV 实际列数 ({maxCols}) 小于配置的 TestItemStartColumn ({config.TestItemStartColumn})");
        if (config.TestItemColumns.Any(c => c < 1 || c > maxCols))
            throw new InvalidDataException("TestItemColumns 中的测项列号超出 CSV 实际列数。");

        var nameHeaders = nameRows.Select(r => GetRow(rows, r - 1)).ToArray();
        var nameSignatures = nameHeaders.Select((h, i) => (Signature: CsvText.Signature(h), Row: nameRows[i]))
            .GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Min(p => p.Row));
        var nameAnchors = nameHeaders.Select(h =>
        {
            int col = Array.FindIndex(h, s => !string.IsNullOrWhiteSpace(s));
            return (Column: col, Value: col < 0 ? "" : h[col].Trim());
        }).Where(a => a.Column >= 0).ToArray();
        var lowRow = config.LowLimitRow > 0 ? GetRow(rows, config.LowLimitRow - 1) : null;
        var highRow = config.HighLimitRow > 0 ? GetRow(rows, config.HighLimitRow - 1) : null;
        var unitRow = config.UnitRow > 0 ? GetRow(rows, config.UnitRow - 1) : null;
        var testNumberRow = config.TestNumberRow > 0 ? GetRow(rows, config.TestNumberRow - 1) : null;
        var sbinHeaderRow = config.SBinRow > 0 ? GetRow(rows, config.SBinRow - 1) : null;
        var hbinHeaderRow = config.HBinRow > 0 ? GetRow(rows, config.HBinRow - 1) : null;

        var items = new List<TestItemData>();
        var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var identityColumns = new[] { config.SerialNumberColumn, config.XColumn, config.YColumn, config.SiteColumn,
            config.WaferColumn, config.SBinColumn, config.HBinColumn, config.ProductColumn, config.ProgramColumn, config.LotColumn }.ToHashSet();
        var parentNames = new string[nameHeaders.Length];

        var measurementColumns = config.TestItemColumns.Count > 0 ? config.TestItemColumns.Select(c => c - 1)
            : Enumerable.Range(config.TestItemStartColumn - 1, maxCols - config.TestItemStartColumn + 1);
        foreach (int col in measurementColumns)
        {
            if (identityColumns.Contains(col + 1)) continue;
            var parts = new List<string>();
            for (int h = 0; h < nameHeaders.Length; h++)
            {
                string part = GetCell(nameHeaders[h], col).Trim();
                if (h < nameHeaders.Length - 1)
                {
                    if (part.Length > 0)
                    {
                        if (part != parentNames[h]) Array.Clear(parentNames, h + 1, parentNames.Length - h - 1);
                        parentNames[h] = part;
                    }
                    else part = parentNames[h] ?? "";
                }
                if (part.Length > 0 && !parts.Contains(part, StringComparer.Ordinal)) parts.Add(part);
            }
            // Retain vertically merged explicit names, but not columns consisting only
            // of an inherited horizontal group label.
            if (!nameHeaders.Any(h => GetCell(h, col).Trim().Length > 0)) continue;
            string name = string.Join(" / ", parts);
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

        int capacity = Math.Max(0, rowCount - config.DataStartRow + 1);
        // Small batch inputs can feed the shared destination directly. RecordList
        // automatically spills after 2048 rows, so large inputs remain bounded.
        var records = new RecordList(deferColumns ? Math.Min(2048, capacity) : capacity, disk: !yieldOnly && !deferColumns);
        var schema = new MeasurementSchema(items.Select(i => i.Id));
        string waferId = Path.GetFileNameWithoutExtension(csvPath);
        string unannotatedWaferId = $"{waferId} (未标注 wafer)";
        var projection = yieldOnly ? new YieldReadProjection(items, config) : null;
        var rawStore = yieldOnly ? null : new RawMeasurementStore();
        try
        {
            progress?.Invoke("正在读取数据行...");
            stamp.Validate(input);
            input.Position = 0;
            using var dataReader = new StreamReader(input, encoding, true, 65536, leaveOpen: true);
            var rowCells = new List<string>(maxCols);
            var quoteBuffer = new StringBuilder(64);
            var rawMeasurements = new string?[items.Count];
            int skippedHeaders = 0;
            using var dataRows = CsvText.ReadRecords(dataReader, config.HasQuotedFields, cancellationToken).GetEnumerator();
            for (int r = 1; r < config.DataStartRow; r++) dataRows.MoveNext();
            var replay = new Queue<string>();
            bool Next(out string line)
            {
                if (replay.Count > 0) { line = replay.Dequeue(); return true; }
                if (dataRows.MoveNext()) { line = dataRows.Current; return true; }
                line = ""; return false;
            }
            while (Next(out string line))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ParseLineInto(line, config.Delimiter, config.HasQuotedFields, rowCells, quoteBuffer);
                if (config.SkipRepeatedHeaders)
                {
                    bool possibleHeader = false;
                    foreach (var anchor in nameAnchors)
                        if (GetCell(rowCells, anchor.Column).Trim() == anchor.Value) { possibleHeader = true; break; }
                    if (possibleHeader)
                    {
                        ulong signature = CsvText.Signature(rowCells);
                        if (nameSignatures.TryGetValue(signature, out int headerRow))
                        {
                            // Only discard a complete matching definition block. Look-ahead is replayed
                            // on mismatch, so real values equal to an LSL/USL row are never swallowed.
                            int following = config.DataStartRow - headerRow - 1;
                            var ahead = new List<string>();
                            bool complete = following <= 256;
                            if (complete)
                                for (int offset = 1; offset <= following; offset++)
                                {
                                    if (!Next(out string next)) { complete = false; break; }
                                    ahead.Add(next);
                                    if (CsvText.Signature(ParseLine(next, config.Delimiter, config.HasQuotedFields)) != definitionSignatures[headerRow + offset - 1])
                                    { complete = false; break; }
                                }
                            if (!complete) replay = new Queue<string>(ahead.Concat(replay));
                            skippedHeaders += 1 + (complete ? ahead.Count : 0);
                            continue;
                        }
                    }
                }

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

                bool nonblank = false;
                for (int i = 0; i < items.Count; i++)
                {
                    rawMeasurements[i] = GetCell(rowCells, items[i].ColumnIndex);
                    nonblank |= !string.IsNullOrWhiteSpace(rawMeasurements[i]);
                }

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
                    HasNonblankMeasurements = nonblank
                };
                if (projection != null) projection.Capture(record, rawMeasurements);
                else record.Measurements = rawStore!.Append(schema, rawMeasurements);
                records.Add(record);
            }

            stamp.Validate(input);
            rawStore?.Seal();
            // A batch will remap definitions and build its numeric columns once.
            // Keep exact raw cells here, without constructing disposable per-file columns.
            var parsed = projection != null ? projection.Complete(records) : deferColumns
                ? new CsvParseResult { Items = items, Records = records, Devices = DatasetService.BuildDevices(records) }
                : DatasetService.Build(items, records);
            if (skippedHeaders > 0) parsed.SourceDescription = $"已跳过 {skippedHeaders} 行重复表头";
            progress?.Invoke($"解析完成，共 {items.Count} 个测试项，{parsed.Devices.Count} 颗唯一芯片");
            return parsed;
        }
        catch { rawStore?.Dispose(); throw; }
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

    internal static string[] ParseLine(string line, char delimiter, bool hasQuoted)
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
