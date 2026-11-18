using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

internal sealed record LayoutPreview(string[][] Rows, ImportWorksheet[] Sheets, string? Worksheet, int UncachedFormulas)
{
    internal static string DisplayValue(string value) => value.Length <= 512 ? value : value[..512] + "…";
}

internal static class IniLayoutPreview
{
    internal const int Limit = 50;

    internal static bool Supports(CsvImportSource source)
    {
        string name = source.ArchiveEntry ?? source.Path;
        return ImportContainerService.IsSpreadsheet(name) ||
            new[] { ".csv", ".txt", ".tsv" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    internal static LayoutPreview Read(CsvImportSource source, AnalyzerConfig config, CancellationToken token)
    {
        if (!Supports(source)) throw new InvalidDataException("请选择普通 CSV 或 Excel 表格作为样本。");
        using var resolved = ImportTemporaryFile.Resolve(source.Path, source.ArchiveEntry, token);
        if (ImportContainerService.IsSpreadsheet(resolved.Path))
            return SpreadsheetImportService.Preview(resolved.Path, source.Worksheet, Limit, Limit, token);

        if (DataFileService.IsStdf(resolved.Path) || IntegratedCsvService.IsIntegrated(resolved.Path))
            throw new InvalidDataException("请选择需要 INI 行列定义的普通 CSV 或 Excel 样本。");

        using var input = SourceFileReader.Open(resolved.Path);
        var stamp = new SourceFileStamp(input);
        using var reader = new StreamReader(input, CsvText.EncodingFor(config.EncodingName), true, 65536);
        var rows = CsvText.ReadRecords(reader, config.HasQuotedFields, token).Take(Limit)
            .Select(line => CsvParser.ParseLine(line, config.Delimiter, config.HasQuotedFields, Limit)
                .Select(LayoutPreview.DisplayValue).ToArray()).ToArray();
        stamp.Validate(input);
        return new(rows, Array.Empty<ImportWorksheet>(), null, 0);
    }
}
