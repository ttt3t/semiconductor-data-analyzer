using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class RetestIdentityService
{
    public static List<string> ParseTestNumbers(string text)
    {
        var result = new List<string>();
        foreach (string part in text.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
                throw new InvalidDataException($"Test Num 必须是非负整数：{part}");
            string key = number.ToString(CultureInfo.InvariantCulture);
            if (result.Contains(key)) throw new InvalidDataException($"UUID Test Num 重复：{key}");
            result.Add(key);
        }
        return result;
    }

    public sealed class Reader
    {
        private readonly string?[] _ids;
        private readonly HashSet<string> _missing;
        public string Recipe { get; }
        public string[] MissingTests { get; }
        public Reader(IReadOnlyList<TestItemData> items, AnalyzerConfig config)
        {
            var numbers = ParseTestNumbers(string.Join(",", config.UuidTestNumbers));
            if (numbers.Count == 0) throw new InvalidDataException("请指定至少一个 UUID Test Num。");
            Recipe = string.Join(",", numbers);
            var byNumber = items.Where(i => ulong.TryParse(i.TestNumber.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .GroupBy(i => ulong.Parse(i.TestNumber.Trim(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
                .ToDictionary(g => g.Key, g => g.ToArray());
            var missingTests = new List<string>();
            _ids = numbers.Select(number =>
            {
                if (!byNumber.TryGetValue(number, out var matches)) { missingTests.Add(number); return null; }
                if (matches.Length != 1) throw new InvalidDataException($"UUID Test Num {number} 对应多个测试项，无法唯一读取，请先修正测试项定义。");
                return matches[0].Id;
            }).ToArray();
            MissingTests = missingTests.ToArray();
            _missing = config.MissingTokens.Concat(config.NotApplicableTokens).Concat(new[] { "NULL", "NaN", "Infinity", "+Infinity", "-Infinity" })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        public string Read(CsvRecord record)
        {
            var value = new StringBuilder();
            bool incomplete = false;
            foreach (string? id in _ids)
            {
                if (id == null) return "";
                if (!record.Measurements.TryGetValue(id, out string? raw))
                { incomplete = true; continue; }
                if (string.IsNullOrWhiteSpace(raw) || _missing.Contains(raw.Trim())) return "";
                value.Append(raw.Trim()); // Preserve hex text, case and leading zeroes; no numeric conversion.
            }
            return incomplete ? record.UuidTestNumbers == Recipe ? record.RetestUuid ?? "" : "" : value.ToString();
        }
    }

    public static CsvParseResult Prepare(CsvParseResult data, AnalyzerConfig config)
    {
        AnalysisOperation.CheckCancellation();
        string signature = config.TestMode + ":" + string.Join(",", config.UuidTestNumbers) + ":" +
            string.Join(",", config.MissingTokens.Concat(config.NotApplicableTokens));
        if (data.RetestIdentitySignature == signature || config.TestMode == TestAnalysisMode.CP && data.Records.All(r => !r.IsFtIdentity)) return data;
        Reader? reader = config.TestMode == TestAnalysisMode.FT ? new(data.Items, config) : null;
        int missing = 0;
        var records = data.Records.Select(source =>
        {
            AnalysisOperation.CheckCancellation();
            var record = DatasetService.CloneRecord(source);
            record.IsFtIdentity = reader != null;
            record.RetestUuid = reader != null ? reader.Read(source) : source.RetestUuid;
            record.UuidTestNumbers = reader?.Recipe ?? source.UuidTestNumbers;
            if (record.IsFtIdentity && record.RetestUuid == "") missing++;
            return record;
        }).ToList();
        // Numeric columns and disk-backed raw measurements stay shared and immutable.
        var result = new CsvParseResult
        {
            Items = data.Items, Records = records, Devices = DatasetService.BuildDevices(records),
            SourceDescription = data.SourceDescription, SerialNumberNotice = data.SerialNumberNotice,
            StdfMetadata = data.StdfMetadata,
            ReassignedSerialNumberCount = data.ReassignedSerialNumberCount,
            RetestIdentitySignature = signature, MissingUuidCount = missing
        };
        if (missing > 0)
            result.RetestIdentityNotice = $"FT UUID：{missing:N0} / {records.Count:N0} 条记录未读全指定值，已独立保留，不使用序列号或坐标合并。" +
                (reader!.MissingTests.Length > 0 ? "\n源数据未找到 Test Num：" + string.Join(", ", reader.MissingTests) : "");
        return result;
    }
}
