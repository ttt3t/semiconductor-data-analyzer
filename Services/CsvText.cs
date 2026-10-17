using System.IO;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>CSV records can span physical lines; configured row numbers count logical records.</summary>
internal static class CsvText
{
    public static Encoding EncodingFor(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(name);
    }

    public static IEnumerable<string> ReadRecords(TextReader reader, bool quoted, CancellationToken token = default,
        int maxRecordCharacters = 8 * 1024 * 1024)
    {
        StringBuilder? joined = null;
        bool inQuotes = false;
        int physicalLine = 0;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            physicalLine++;
            if (line.Length + (joined?.Length ?? 0) > maxRecordCharacters)
                throw new InvalidDataException($"CSV 第 {physicalLine} 行记录过长，请检查引号是否闭合。");
            if (quoted)
                for (int i = 0; i < line.Length; i++)
                    if (line[i] == '"')
                    {
                        if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') i++;
                        else inQuotes = !inQuotes;
                    }
            if (joined != null) joined.Append('\n').Append(line);
            else if (inQuotes) joined = new StringBuilder(line);
            if (inQuotes) continue;
            yield return joined?.ToString() ?? line;
            joined = null;
        }
        if (inQuotes) throw new InvalidDataException($"CSV 引号未闭合（文件结束于第 {physicalLine} 行）。");
    }

    public static int[] NameRows(Models.AnalyzerConfig config) => config.TestItemRows.Count > 0
        ? config.TestItemRows.Distinct().Order().ToArray() : new[] { config.TestItemRow };

    public static ulong Signature(IEnumerable<string> cells)
    {
        ulong hash = 14695981039346656037;
        foreach (string cell in cells)
        {
            foreach (char c in cell.AsSpan().Trim()) hash = unchecked((hash ^ c) * 1099511628211);
            hash = unchecked((hash ^ 0xffff) * 1099511628211);
        }
        return hash;
    }
}
