using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Both formats enter the same dataset/analysis pipeline.</summary>
public static class DataFileService
{
    public const string OpenFilter = "测试数据 (*.csv;*.std;*.stdf;*.std.gz;*.stdf.gz)|*.csv;*.std;*.stdf;*.std.gz;*.stdf.gz|CSV 文件 (*.csv)|*.csv|STDF 文件 (*.std;*.stdf;*.std.gz;*.stdf.gz)|*.std;*.stdf;*.std.gz;*.stdf.gz|所有文件|*.*";

    public static bool IsStdf(string path)
    {
        string name = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        if (name.EndsWith(".std", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".stdf", StringComparison.OrdinalIgnoreCase)) return true;
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return file.Read(header) == 4 && header[2] == 0 && header[3] == 10 &&
            (header[0] == 2 && header[1] == 0 || header[0] == 0 && header[1] == 2);
    }

    public static CsvParseResult Parse(string path, AnalyzerConfig config, Action<string>? progress = null,
        CancellationToken cancellationToken = default, StdfImportMode stdfMode = StdfImportMode.Standard)
    {
        bool stdf = IsStdf(path);
        bool integrated = !stdf && IntegratedCsvService.IsIntegrated(path);
        var result = stdf ? StdfParser.Parse(path, config, progress, cancellationToken, stdfMode)
            : CsvParser.Parse(path, config, progress);
        if (SerialNumberService.Normalize(result, !stdf && !integrated && config.SerialNumberColumn <= 0))
            result.SerialNumberNotice = $"文件“{Path.GetFileName(path)}”：{result.SerialNumberNotice}";
        return result;
    }
}

