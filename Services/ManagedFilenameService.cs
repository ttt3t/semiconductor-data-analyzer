using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class ManagedFilenameService
{
    private static readonly string[] Formats = {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
        "yyyyMMdd_HHmmss", "yyyyMMdd-HHmmss", "yyyyMMddHHmmss", "yyyy-MM-dd_HH-mm-ss",
        "yyyy-MM-dd_HHmmss", "yyyyMMdd_HHmm", "yyyyMMddHHmm", "yyyy-MM-dd", "yyyy_MM_dd", "yyyyMMdd", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm" };
    private static readonly Regex Dates = new(@"(?<!\d)(?:\d{4}-\d{2}-\d{2}(?:[T _]\d{2}[:\-]\d{2}(?:[:\-]\d{2})?|_\d{6})?|\d{4}_\d{2}_\d{2}|\d{8}(?:[_-]\d{6}|_\d{4})?|\d{14}|\d{12})(?!\d)", RegexOptions.CultureInvariant);

    public static string Stem(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Slice(string stem, int start, int length)
    {
        if (start < 1 || length < 1 || start > stem.Length || length > stem.Length - (start - 1))
            throw new InvalidDataException("字符范围超出文件名；起始位置从 1 开始，长度必须大于 0。");
        return stem.Substring(start - 1, length).Trim();
    }

    public static DateTime? ParseTime(string? text, string format = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (format.Length > 0)
            {
                string fields = Regex.Replace(format, "'[^']*'|\"[^\"]*\"|\\\\.", "");
                if (!fields.Contains('y') || !fields.Contains('M') || !fields.Contains('d'))
                    throw new InvalidDataException("自定义时间格式需同时包含年、月、日（例如 yyyyMMdd_HHmmss），不能用当前日期补齐。");
            }
            if (DateTime.TryParseExact(text.Trim(), format.Length > 0 ? new[] { format } : Formats,
                CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var date)) return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }
        catch (FormatException ex) { throw new InvalidDataException("自定义时间格式无效。", ex); }
        throw new InvalidDataException("时间格式无效，请填写 yyyy-MM-dd HH:mm:ss、yyyy-MM-dd 或指定准确的格式。");
    }

    public static DateTime? ExtractTime(string stem, ManagedFilenameRule rule)
    {
        if (rule.TimeLength > 0) return ParseTime(Slice(stem, rule.TimeStart, rule.TimeLength), rule.TimeFormat);
        var dates = Dates.Matches(stem).Select(m =>
        {
            try { return ParseTime(m.Value); } catch (InvalidDataException) { return null; }
        }).Where(d => d.HasValue).Select(d => d!.Value).Distinct().ToArray();
        if (dates.Length > 1) throw new InvalidDataException("文件名包含多个不同时间，请指定时间字符范围或手动填写。");
        return dates.Length == 1 ? dates[0] : null;
    }

    public static string Apply(ManagedDataFile file, ManagedFilenameRule rule, bool overwrite = false)
    {
        if (file.FilePath.Length == 0) return "";
        string stem = Stem(file.FilePath); var warnings = new List<string>();
        if (rule.ExtractWafer && (overwrite || file.WaferOverride.Length == 0))
        {
            try { file.WaferOverride = Slice(stem, rule.WaferStart, rule.WaferLength); }
            catch (InvalidDataException ex) { warnings.Add("片号：" + ex.Message); }
        }
        if (rule.ExtractTime && (overwrite || !file.TestTime.HasValue))
        {
            try
            {
                var time = ExtractTime(stem, rule);
                if (time.HasValue) file.TestTime = time;
                else warnings.Add("未识别时间，可手动填写");
            }
            catch (InvalidDataException ex) { warnings.Add(ex.Message); }
        }
        return string.Join("；", warnings);
    }
}
