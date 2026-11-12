using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Both formats enter the same dataset/analysis pipeline.</summary>
public static class DataFileService
{
    public const string OpenFilter = "测试数据 (*.csv;*.std;*.stdf;*.std.gz;*.stdf.gz;*.xlsx;*.xlsm;*.zip)|*.csv;*.std;*.stdf;*.std.gz;*.stdf.gz;*.xlsx;*.xlsm;*.zip|Excel 工作簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|ZIP 压缩包 (*.zip)|*.zip|CSV 文件 (*.csv)|*.csv|STDF 文件 (*.std;*.stdf;*.std.gz;*.stdf.gz)|*.std;*.stdf;*.std.gz;*.stdf.gz|所有文件|*.*";

    public static bool NeedsLayout(CsvImportSource source)
    {
        if (ImportContainerService.IsSpreadsheet(source.ArchiveEntry ?? source.Path)) return true;
        using var resolved = ImportTemporaryFile.Resolve(source.Path, source.ArchiveEntry, AnalysisOperation.Token);
        return !IsStdf(resolved.Path) && !IntegratedCsvService.IsIntegrated(resolved.Path);
    }

    public static bool IsStdf(string path)
    {
        string name = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        if (name.EndsWith(".std", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".stdf", StringComparison.OrdinalIgnoreCase)) return true;
        using var file = SourceFileReader.Open(path);
        Span<byte> header = stackalloc byte[4];
        return file.Read(header) == 4 && header[2] == 0 && header[3] == 10 &&
            (header[0] == 2 && header[1] == 0 || header[0] == 0 && header[1] == 2);
    }

    public static CsvParseResult Parse(string path, AnalyzerConfig config, Action<string>? progress = null,
        CancellationToken cancellationToken = default, StdfImportMode stdfMode = StdfImportMode.Standard,
        string? archiveEntry = null, string? worksheet = null)
        => ParseContainer(path, config, progress, cancellationToken, stdfMode, false, archiveEntry, worksheet);

    internal static CsvParseResult ReadForYield(string path, AnalyzerConfig config, CancellationToken cancellationToken,
        StdfImportMode stdfMode = StdfImportMode.Standard, string? archiveEntry = null, string? worksheet = null)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        using var operation = new AnalysisOperation(cancellationToken);
        return ParseContainer(path, config, null, cancellationToken, stdfMode, true, archiveEntry, worksheet);
    }

    private static CsvParseResult ParseContainer(string path, AnalyzerConfig config, Action<string>? progress,
        CancellationToken cancellationToken, StdfImportMode mode, bool yieldOnly, string? archiveEntry, string? worksheet)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        using var overall = ImportProgress.Stage(0, 1, ImportContainerService.DisplayName(new(path, ArchiveEntry: archiveEntry, Worksheet: worksheet)));
        ImportTemporaryFile resolved;
        using (ImportProgress.Stage(0, archiveEntry == null ? 0 : .15, "读取源文件"))
            resolved = ImportTemporaryFile.Resolve(path, archiveEntry, cancellationToken);
        using (resolved)
        using (ImportProgress.Stage(archiveEntry == null ? 0 : .15, 1, ""))
        {
            CsvParseResult data;
            if (ImportContainerService.IsSpreadsheet(resolved.Path))
            {
                SpreadsheetImportService.Converted converted;
                using (ImportProgress.Stage(0, .35, "读取 Excel")) converted = SpreadsheetImportService.Convert(resolved.Path, worksheet, cancellationToken);
                using (converted)
                using (ImportProgress.Stage(.35, 1, "分析 Excel 数据"))
                {
                    var layout = ConfigLoader.Parse(ConfigLoader.Serialize(config), config.ConfigFileName);
                    layout.EncodingName = "UTF-8"; layout.Delimiter = ','; layout.HasQuotedFields = true;
                    data = ParseCore(converted.File.Path, layout, progress, cancellationToken, mode, yieldOnly);
                    data.SourceDescription = $"Excel：{Path.GetFileName(path)} / {converted.Worksheet}；使用已保存的单元格值，不执行宏或重算公式。";
                    if (converted.UncachedFormulas > 0)
                        data.ImportWarning = (data.ImportWarning.Length == 0 ? "" : data.ImportWarning + Environment.NewLine) +
                            $"{converted.UncachedFormulas} 个公式没有保存的计算结果，已按缺失数据导入；可在 Excel 中重算并保存后再导入。";
                }
            }
            else data = ParseCore(resolved.Path, config, progress, cancellationToken, mode, yieldOnly);
            if (archiveEntry != null) data.SourceDescription = $"ZIP：{Path.GetFileName(path)} ▸ {archiveEntry}" +
                (data.SourceDescription.Length > 0 ? " | " + data.SourceDescription : "");
            return data;
        }
    }

    private static CsvParseResult ParseCore(string path, AnalyzerConfig config, Action<string>? progress,
        CancellationToken cancellationToken, StdfImportMode stdfMode, bool yieldOnly)
    {
        using var fileProgress = ImportProgress.Stage(0, 1, Path.GetFileName(path));
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        bool stdf = IsStdf(path);
        bool integrated = !stdf && IntegratedCsvService.IsIntegrated(path);
        CsvParseResult result;
        using (ImportProgress.Stage(0, .96, ""))
            result = stdf ? StdfParser.ParseCore(path, config, progress, cancellationToken, stdfMode, yieldOnly)
                : CsvParser.ParseCore(path, config, progress, cancellationToken, yieldOnly);
        using (ImportProgress.Stage(.96, 1, "匹配芯片身份"))
        {
            if (SerialNumberService.Normalize(result, !stdf && !integrated && config.SerialNumberColumn <= 0))
                result.SerialNumberNotice = $"文件“{Path.GetFileName(path)}”：{result.SerialNumberNotice}";
            result = yieldOnly ? YieldReadProjection.PrepareIdentity(result, config) : RetestIdentityService.Prepare(result, config);
            ImportProgress.Report(1, true);
            return result;
        }
    }
}

