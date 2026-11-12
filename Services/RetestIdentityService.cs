using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class RetestIdentityService
{
    public const int CurrentEncoding = 1;
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
        private readonly string[][] _ids;
        private readonly HashSet<string> _missing;
        public string Recipe { get; }
        public string[] MissingTests { get; }
        public Reader(IReadOnlyList<TestItemData> items, AnalyzerConfig config)
        {
            var numbers = ParseTestNumbers(string.Join(",", config.UuidTestNumbers));
            if (numbers.Count == 0) throw new InvalidDataException("请指定至少一个 UUID Test Num。");
            Recipe = string.Join(",", numbers);
            var byNumber = items.Where(i => ulong.TryParse(i.BaseTestNumber, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .GroupBy(i => ulong.Parse(i.BaseTestNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
                .ToDictionary(g => g.Key, g => g.ToArray());
            var missingTests = new List<string>();
            _ids = numbers.Select(number =>
            {
                if (!byNumber.TryGetValue(number, out var matches)) { missingTests.Add(number); return Array.Empty<string>(); }
                if (matches.Length != 1 && !(matches.All(i => i.UnitMergeSlot > 0) &&
                    matches.Select(i => (i.Name, i.StdfChannel, i.UnitMergeSlot)).Distinct().Count() == 1))
                    throw new InvalidDataException($"UUID Test Num {number} 对应多个测试项，无法唯一读取，请先修正测试项定义。");
                return matches.Select(i => i.Id).ToArray();
            }).ToArray();
            MissingTests = missingTests.ToArray();
            _missing = config.MissingTokens.Concat(config.NotApplicableTokens).Concat(new[] { "NULL", "NaN", "Infinity", "+Infinity", "-Infinity", StdfParser.NotApplicableValue })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        public string Read(CsvRecord record)
        {
            if (record.PreferCachedUuid && record.UuidTestNumbers == Recipe)
            {
                if (string.IsNullOrWhiteSpace(record.RetestUuid)) return "";
                string cached = record.RetestUuid.Trim();
                return (_ids.Length == 1 ? !_missing.Contains(cached) && !cached.StartsWith("STDF_INVALID(", StringComparison.Ordinal) :
                    record.UuidEncoding == CurrentEncoding && ValidCachedTuple(cached)) ? cached : "";
            }
            var value = new StringBuilder();
            bool incomplete = false;
            foreach (var alternatives in _ids)
            {
                string? raw = null;
                foreach (string id in alternatives)
                    if (record.Measurements.TryGetOriginalText(id, out var text))
                    {
                        string candidate = text.ToString();
                        if (raw != null && raw.Trim() != candidate.Trim()) return "";
                        raw = candidate;
                    }
                if (raw == null) { incomplete = true; continue; }
                if (string.IsNullOrWhiteSpace(raw) || _missing.Contains(raw.Trim()) || raw.Trim().StartsWith("STDF_INVALID(", StringComparison.Ordinal)) return "";
                string field = raw.Trim(); // Preserve hex text, case and leading zeroes; no numeric conversion.
                if (_ids.Length > 1)
                {
                    if (value.Length > 0) value.Append('|');
                    value.Append(field.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal));
                }
                else value.Append(field);
            }
            if (!incomplete) return value.ToString();
            // Legacy multi-field concatenations cannot be split reliably. Never reuse them as verified IDs.
            if (record.UuidTestNumbers != Recipe || string.IsNullOrWhiteSpace(record.RetestUuid)) return "";
            if (_ids.Length == 1) return record.RetestUuid!;
            return record.UuidEncoding == CurrentEncoding && ValidCachedTuple(record.RetestUuid!) ? record.RetestUuid! : "";
        }

        private bool ValidCachedTuple(string text)
        {
            var field = new StringBuilder(); int count = 0;
            bool ValidField() => field.Length > 0 && !string.IsNullOrWhiteSpace(field.ToString()) &&
                !_missing.Contains(field.ToString().Trim()) && !field.ToString().StartsWith("STDF_INVALID(", StringComparison.Ordinal);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (++i == text.Length || text[i] is not ('\\' or '|')) return false;
                    field.Append(text[i]);
                }
                else if (c == '|')
                {
                    if (!ValidField()) return false;
                    count++; field.Clear();
                }
                else field.Append(c);
            }
            return ValidField() && count + 1 == _ids.Length;
        }
    }

    public static CsvParseResult Prepare(CsvParseResult data, AnalyzerConfig config)
    {
        AnalysisOperation.CheckCancellation();
        string signature = "UUID" + CurrentEncoding + ":" + config.TestMode + ":" + string.Join(",", config.UuidTestNumbers) + ":" +
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
            record.UuidEncoding = reader != null ? CurrentEncoding : source.UuidEncoding;
            if (record.IsFtIdentity && record.RetestUuid == "") missing++;
            return record;
        }).ToList();
        // Numeric columns and disk-backed raw measurements stay shared and immutable.
        var result = new CsvParseResult
        {
            Items = data.Items, Records = records, Devices = DatasetService.BuildDevices(records),
            SourceDescription = data.SourceDescription, SerialNumberNotice = data.SerialNumberNotice,
            ImportWarning = data.ImportWarning,
            StdfMetadata = data.StdfMetadata,
            StdfWaferOrigins = data.StdfWaferOrigins,
            ReassignedSerialNumberCount = data.ReassignedSerialNumberCount,
            RetestIdentitySignature = signature, MissingUuidCount = missing
        };
        if (missing > 0)
            result.RetestIdentityNotice = $"FT UUID：{missing:N0} / {records.Count:N0} 条记录未读全指定值，已独立保留，不使用序列号或坐标合并。" +
                (reader!.MissingTests.Length > 0 ? "\n源数据未找到 Test Num：" + string.Join(", ", reader.MissingTests) : "");
        return result;
    }
}
