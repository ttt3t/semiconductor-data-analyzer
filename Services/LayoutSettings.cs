using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class ColumnLayout
{
    public string Key { get; set; } = "";
    public int DisplayIndex { get; set; }
    public double Width { get; set; }
    public DataGridLengthUnitType WidthUnit { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Visible;
}

public sealed class SortLayout
{
    public string Property { get; set; } = "";
    public ListSortDirection Direction { get; set; }
}

public sealed class TableLayout
{
    public List<ColumnLayout> Columns { get; set; } = new();
    public List<SortLayout> Sorts { get; set; } = new();
}

public sealed class WindowLayout
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public Dictionary<string, TableLayout> Tables { get; set; } = new();
    public Dictionary<string, double> SplitRatios { get; set; } = new();
}

/// <summary>Layout is independent of CSV/configuration data and belongs to the current Windows user.</summary>
public sealed class LayoutSettings
{
    public static LayoutSettings Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SemiconductorCsvAnalyzer", "layout.settings.json"));

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, WindowLayout> Windows { get; set; } = new();
    }

    private readonly string _path;
    private Document _document = new();
    public event Action<string>? SaveFailed;

    public LayoutSettings(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (document is { Version: 1, Windows: not null })
                    _document = document;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing, unreadable or damaged layout must never prevent the application from opening.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public WindowLayout? Get(string key) => _document.Windows.GetValueOrDefault(key);

    public void Save(string key, WindowLayout layout)
    {
        _document.Windows[key] = layout;
        string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_document,
                new JsonSerializerOptions { WriteIndented = true }));
            // Replace only after the complete document has been written.
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke("布局保存失败：" + ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
