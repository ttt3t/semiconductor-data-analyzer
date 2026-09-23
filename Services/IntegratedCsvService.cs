using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class IntegratedCsvService
{
    private const string Marker = "#CSVAnalyzerIntegrated,1";
    private const string Marker2 = "#CSVAnalyzerIntegrated,2";
    private const string Marker3 = "#CSVAnalyzerIntegrated,3";
    private const int IdentityColumns = 10;
    private static readonly string[] ContextHeaders = { "Product", "Program", "Lot" };
    private static readonly string[] SourceSerialHeaders = { "OriginalSerialNumber", "SerialNumberReassigned" };
    private static readonly string[] Headers = { "Wafer", "RecordId", "SerialNumber", "X", "Y", "Site", "Result", "SBIN", "HBIN", "MeasuredTests" };

    public static bool IsIntegrated(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadLine() is Marker or Marker2 or Marker3;
    }

    public static void Save(string path, CsvParseResult data)
        => SaveRows(path, data.Items, data.Records);

    public static int Save(string path, CsvParseResult data, IReadOnlyCollection<string> wafers, RetestMode mode,
        double? outlierSigma = null)
    {
        if (outlierSigma.HasValue && (!double.IsFinite(outlierSigma.Value) || outlierSigma.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(outlierSigma), "离群阈值必须大于 0 且为有限数字。");
        var records = SelectRecords(data, wafers, mode);
        var bounds = outlierSigma.HasValue ? CalculateOutlierBounds(data.Items, records, outlierSigma.Value) : null;
        SaveRows(path, data.Items, records, bounds);
        return records.Count;
    }

    internal static List<CsvRecord> SelectRecords(CsvParseResult data, IReadOnlyCollection<string> wafers, RetestMode mode)
    {
        var selected = wafers.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Except(data.Wafers).Any())
            throw new InvalidDataException("请至少选择一个有效晶圆。");
        return DatasetService.SelectRetestRecords(data.Records.Where(r => selected.Contains(r.WaferId)).ToList(), mode);
    }

    internal static (double Lower, double Upper)[] CalculateOutlierBounds(IReadOnlyList<TestItemData> items,
        IReadOnlyList<CsvRecord> records, double multiplier)
    {
        // Row-order reads reuse the disk snapshot's row cache. Only one accumulator
        // per item is retained; do not rebuild columns or allocate per-cell objects.
        var stats = new (int Count, double Mean, double M2)[items.Count];
        foreach (var record in records)
            for (int i = 0; i < items.Count; i++)
                if (record.Measurements.TryGetText(items[i].Id, out var raw) && DatasetService.TryValue(raw, out double value))
                {
                    ref var stat = ref stats[i];
                    stat.Count++;
                    double delta = value - stat.Mean;
                    stat.Mean += delta / stat.Count;
                    stat.M2 += delta * (value - stat.Mean);
                }
        return stats.Select(stat =>
        {
            double radius = stat.Count == 0 ? double.NaN : multiplier * Math.Sqrt(Math.Max(0, stat.M2 / stat.Count));
            // No numeric population (or overflow): never discard values on an undefined bound.
            return stat.Count == 0 || !double.IsFinite(stat.Mean) || double.IsNaN(radius)
                ? (double.NegativeInfinity, double.PositiveInfinity) : (stat.Mean - radius, stat.Mean + radius);
        }).ToArray();
    }

    private static void SaveRows(string path, IReadOnlyList<TestItemData> items, IReadOnlyList<CsvRecord> records,
        (double Lower, double Upper)[]? outlierBounds = null)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                bool sourceSerial = records.Any(r => r.OriginalSerialNumber != null);
                bool context = sourceSerial || records.Any(r => r.Product.Length + r.Program.Length + r.Lot.Length > 0);
                int identityCount = IdentityColumns + (context ? 3 : 0) + (sourceSerial ? 2 : 0);
                writer.WriteLine(sourceSerial ? Marker3 : context ? Marker2 : Marker);
                void Row(IEnumerable<string> fields) => writer.WriteLine(string.Join(",", fields.Select(Escape)));
                void Metadata(string label, Func<TestItemData, string> value) =>
                    Row(new[] { label }.Concat(Enumerable.Repeat("", identityCount - 1)).Concat(items.Select(value)));
                Row(Headers.Concat(context ? ContextHeaders : Array.Empty<string>())
                    .Concat(sourceSerial ? SourceSerialHeaders : Array.Empty<string>()).Concat(items.Select(i => i.Name)));
                Metadata("#TestNumber", i => i.TestNumber);
                Metadata("#LSL", i => Number(i.LowLimit));
                Metadata("#USL", i => Number(i.HighLimit));
                Metadata("#Unit", i => i.Unit);
                Metadata("#SBIN", i => i.SBin);
                Metadata("#HBIN", i => i.HBin);
                Metadata("#DisplayName", i => i.DisplayName);
                foreach (var record in records)
                {
                    // Presence distinguishes an untested item from an actually blank measurement.
                    var presence = new char[items.Count];
                    var values = new string[items.Count];
                    for (int i = 0; i < items.Count; i++)
                    {
                        bool present = record.Measurements.TryGetText(items[i].Id, out var raw);
                        if (present && outlierBounds != null && DatasetService.TryValue(raw, out double value) &&
                            (value < outlierBounds[i].Lower || value > outlierBounds[i].Upper)) present = false;
                        // Removed values are absent, not new blank-value errors. Do not mutate the source.
                        presence[i] = present ? '1' : '0';
                        values[i] = present ? raw.ToString() : "";
                    }
                    Row(new[] { record.WaferId, record.Id, record.SerialNumber, record.X?.ToString(CultureInfo.InvariantCulture) ?? "",
                        record.Y?.ToString(CultureInfo.InvariantCulture) ?? "", record.Site, record.Result, record.SBin, record.HBin, new string(presence) }
                        .Concat(context ? new[] { record.Product, record.Program, record.Lot } : Array.Empty<string>())
                        .Concat(sourceSerial ? new[] { record.OriginalSerialNumber ?? "", record.OriginalSerialNumber != null ? "1" : "0" } : Array.Empty<string>()).Concat(values));
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static CsvParseResult Load(string path)
    {
        using var reader = new TextFieldParser(path, Encoding.UTF8)
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var marker = reader.ReadFields();
        bool sourceSerial = marker != null && string.Join(",", marker) == Marker3;
        bool context = sourceSerial || marker != null && string.Join(",", marker) == Marker2;
        int identityCount = IdentityColumns + (context ? 3 : 0) + (sourceSerial ? 2 : 0);
        if (marker == null || (!context && string.Join(",", marker) != Marker)) throw new InvalidDataException("不支持的整合 CSV 格式。");
        var header = reader.ReadFields() ?? throw new InvalidDataException("整合 CSV 缺少表头。");
        if (header.Length < identityCount || !header.Take(identityCount).SequenceEqual(Headers.Concat(context ? ContextHeaders : Array.Empty<string>())
            .Concat(sourceSerial ? SourceSerialHeaders : Array.Empty<string>())))
            throw new InvalidDataException("整合 CSV 表头不完整。");
        string[] Metadata(string name)
        {
            var row = reader.ReadFields();
            if (row == null || row.Length != header.Length || row[0] != name)
                throw new InvalidDataException($"整合 CSV 缺少或损坏了 {name} 定义。");
            return row;
        }
        var numbers = Metadata("#TestNumber"); var lsl = Metadata("#LSL"); var usl = Metadata("#USL");
        var units = Metadata("#Unit"); var sbin = Metadata("#SBIN"); var hbin = Metadata("#HBIN"); var names = Metadata("#DisplayName");
        double? Limit(string text) => text.Length == 0 ? null : DatasetService.TryValue(text, out double value)
            ? value : throw new InvalidDataException("整合 CSV 的阈值不是有效数字。");
        var items = Enumerable.Range(identityCount, header.Length - identityCount).Select(c => new TestItemData
        {
            ColumnIndex = c, Name = header[c], DisplayName = names[c], TestNumber = numbers[c],
            LowLimit = Limit(lsl[c]), HighLimit = Limit(usl[c]), Unit = units[c], SBin = sbin[c], HBin = hbin[c]
        }).ToList();
        int? Coordinate(string text) => text.Length == 0 ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new InvalidDataException("整合 CSV 的坐标无效。");
        var records = new List<CsvRecord>();
        var schema = new MeasurementSchema(items.Select(i => i.Id));
        var ids = new HashSet<string>();
        var rawStore = new RawMeasurementStore();
        try
        {
            while (reader.ReadFields() is { } row)
            {
                if (row.Length != header.Length || string.IsNullOrWhiteSpace(row[0]) || string.IsNullOrEmpty(row[1]) || !ids.Add(row[1]) ||
                    row[9].Length != items.Count || row[9].Any(c => c != '0' && c != '1') ||
                    sourceSerial && (row[14] is not ("0" or "1") || row[14] == "0" && row[13].Length > 0))
                    throw new InvalidDataException("整合 CSV 的数据行、wafer、记录标识或测试项标识无效。");
                var record = new CsvRecord
                {
                    WaferId = row[0], Id = row[1], SerialNumber = row[2], X = Coordinate(row[3]), Y = Coordinate(row[4]),
                    OriginalSerialNumber = sourceSerial && row[14] == "1" ? row[13] : null,
                    Site = row[5], Result = row[6], SBin = row[7], HBin = row[8],
                    Product = context ? row[10] : "", Program = context ? row[11] : "", Lot = context ? row[12] : ""
                };
                var measurements = new string?[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    if (row[9][i] == '1') measurements[i] = row[identityCount + i];
                    else if (row[identityCount + i].Length != 0) throw new InvalidDataException("未测试项包含测量值，整合 CSV 数据不一致。");
                }
                record.Measurements = rawStore.Append(schema, measurements);
                records.Add(record);
            }
            rawStore.Seal();
            return DatasetService.Build(items, records);
        }
        catch { rawStore.Dispose(); throw; }
    }

    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Escape(string value) => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
