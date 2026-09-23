using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<StdfImportMode>(allowIntegerValues: false) }
    };
    public static UiPreferencesService Current { get; } = new();
    private readonly string _path;
    private UiPreferences _value;
    public UiPreferences Value => _value.Clone();
    public event EventHandler? Changed;

    public UiPreferencesService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SemiconductorCsvAnalyzer", "ui.preferences.json");
        try
        {
            _value = File.Exists(_path)
                ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
            if (!Enum.IsDefined(_value.DefaultStdfMode)) _value = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _value = new(); }
    }

    /// <summary>Commit to disk before changing the active value or notifying windows.</summary>
    public void Save(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var next = preferences.Clone();
        if (!Enum.IsDefined(next.DefaultStdfMode))
            throw new ArgumentException("不支持的 STDF 默认导入模式。", nameof(preferences));
        string path = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _value = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
