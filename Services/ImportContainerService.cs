using System.IO;
using System.IO.Compression;

namespace SemiconductorCsvAnalyzer.Services;

public static class ImportContainerService
{
    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public static bool IsSpreadsheet(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase);
    public static bool IsStdfName(string path) => new[] { ".std", ".stdf", ".std.gz", ".stdf.gz" }
        .Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    public static StdfFileMetadata ReadStdfMetadata(string path, string? entry, CancellationToken token)
    {
        using var resolved = ImportTemporaryFile.Resolve(path, entry, token);
        return StdfMetadataService.Read(resolved.Path, token);
    }
    public static bool IsSupportedEntry(string path) => new[] { ".csv", ".txt", ".tsv", ".std", ".stdf", ".std.gz", ".stdf.gz", ".xlsx", ".xlsm" }
        .Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    public static string EntryName(string path) => System.IO.Path.GetFileName(path.Replace('\\', '/'));
    public static string DisplayName(CsvImportSource source) => System.IO.Path.GetFileName(source.Path) +
        (source.ArchiveEntry == null ? "" : " ▸ " + source.ArchiveEntry) +
        (source.Worksheet == null ? "" : " ▸ " + source.Worksheet);

    public static CsvImportSource[] ZipEntries(string path, CancellationToken token = default)
    {
        using var file = SourceFileReader.Open(path);
        var stamp = new SourceFileStamp(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var sources = new List<CsvImportSource>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0 || !IsSupportedEntry(entry.FullName) ||
                entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || entry.Name.StartsWith("~$", StringComparison.Ordinal)) continue;
            if (!names.Add(entry.FullName)) throw new InvalidDataException($"ZIP 内存在重名路径，无法唯一选择：{entry.FullName}");
            sources.Add(new(path, ArchiveEntry: entry.FullName));
        }
        stamp.Validate(file);
        if (sources.Count == 0) throw new InvalidDataException($"压缩包“{System.IO.Path.GetFileName(path)}”中没有可导入的 CSV、STDF 或 Excel 文件。");
        return sources.ToArray();
    }
}

/// <summary>Only selected members are expanded, to private random directories. Source paths are retained for reloads.</summary>
internal sealed class ImportTemporaryFile : IDisposable
{
    private readonly string? _directory;
    public string Path { get; }
    private ImportTemporaryFile(string path, string? directory = null) { Path = path; _directory = directory; }

    public static ImportTemporaryFile Create(string name)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SemiconductorCsvAnalyzer", "Imports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string safe = new(ImportContainerService.EntryName(name).Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "input.csv";
        if (safe.Length > 180) safe = safe[..140] + System.IO.Path.GetExtension(safe);
        return new(System.IO.Path.Combine(directory, safe), directory);
    }

    public static ImportTemporaryFile Resolve(string path, string? entryName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entryName == null)
        {
            if (ImportContainerService.IsZip(path)) throw new InvalidDataException("请在 ZIP 文件列表中选择要导入的内部文件。");
            return new(path);
        }
        if (!ImportContainerService.IsZip(path)) throw new InvalidDataException("压缩包内部路径只能用于 ZIP 文件。");
        var temporary = Create(entryName);
        try
        {
            using var file = SourceFileReader.Open(path);
            var stamp = new SourceFileStamp(file);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var matches = archive.Entries.Where(e => e.FullName == entryName).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"ZIP 中的路径已不存在或不唯一：{entryName}");
            var entry = matches[0];
            if (entry.Name.Length == 0) throw new InvalidDataException("选择的 ZIP 项目是目录。");
            using (ImportProgress.Stage(0, 1, "展开 " + entryName))
            using (var input = entry.Open())
            using (var output = new FileStream(temporary.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                var buffer = new byte[65536];
                long copied = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count); copied += count;
                    ImportProgress.Report((double)copied / Math.Max(1, entry.Length));
                }
            }
            stamp.Validate(file);
            return temporary;
        }
        catch { temporary.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_directory == null) return;
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
