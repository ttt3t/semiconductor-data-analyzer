using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record RecentFileEntry(string FilePath, DateTime LastUsedUtc)
{
    [JsonIgnore] public string Name => Path.GetFileName(FilePath);
    [JsonIgnore] public string Status => File.Exists(FilePath) ? "" : "文件不存在或暂不可访问";
}

public sealed class RecentFilesService
{
    public static RecentFilesService Current { get; } = new();
    private const int Limit = 20;
    private readonly string _path;
    private sealed class History
    {
        public List<RecentFileEntry> Configs { get; set; } = new();
        public List<RecentFileEntry> Csvs { get; set; } = new();
    }
    private readonly History _history;
    public IReadOnlyList<RecentFileEntry> Configs => _history.Configs.ToArray();
    public IReadOnlyList<RecentFileEntry> Csvs => _history.Csvs.ToArray();

    public RecentFilesService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SemiconductorCsvAnalyzer", "recent-files.json");
        try { _history = File.Exists(_path) ? JsonSerializer.Deserialize<History>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _history = new(); }
        _history.Configs = Clean(_history.Configs); _history.Csvs = Clean(_history.Csvs);
    }

    private static List<RecentFileEntry> Clean(List<RecentFileEntry>? entries)
    {
        var result = new List<RecentFileEntry>();
        foreach (var entry in entries ?? new())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath)) continue;
            try
            {
                string path = Path.GetFullPath(entry.FilePath);
                if (!result.Any(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    result.Add(entry with { FilePath = path });
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (result.Count == Limit) break;
        }
        return result;
    }

    public bool RememberConfig(string path) => Remember(_history.Configs, path);
    public bool RememberCsv(string path) => Remember(_history.Csvs, path);
    private bool Remember(List<RecentFileEntry> entries, string path)
    {
        string? temporary = null;
        try
        {
            path = Path.GetFullPath(path);
            entries.RemoveAll(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, new(path, DateTime.UtcNow));
            if (entries.Count > Limit) entries.RemoveRange(Limit, entries.Count - Limit);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_history));
            File.Move(temporary, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { System.Diagnostics.Debug.WriteLine(ex); return false; }
        finally
        {
            if (temporary != null) try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
