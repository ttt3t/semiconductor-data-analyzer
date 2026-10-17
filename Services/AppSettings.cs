using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class AppSettings
{
    private static string _userDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorCsvAnalyzer");
    private static string _legacyDirectory = AppDomain.CurrentDomain.BaseDirectory;
    public static string SettingsPath =>
        Path.Combine(_userDirectory, "app.settings.ini");

    public static string LastConfigCopyPath =>
        Path.Combine(_userDirectory, "last_config.ini");

    public static string? LastConfigPath { get; private set; }
    public static string LoadNotice { get; private set; } = "";

    public static void Load()
    {
        LastConfigPath = null; LoadNotice = "";
        bool hasUserSettings = File.Exists(SettingsPath);
        string legacy = Path.Combine(_legacyDirectory, "app.settings.ini");
        string path = hasUserSettings ? SettingsPath : File.Exists(legacy) ? legacy :
            Path.Combine(_legacyDirectory, "app.settings.txt");
        try
        {
            LoadPath(path);
            // Preserve an existing user configuration; migrate the previous portable copy only once.
            string oldCopy = Path.Combine(_legacyDirectory, "last_config.ini");
            if (!hasUserSettings && !File.Exists(LastConfigCopyPath) && File.Exists(oldCopy))
                SaveConfig(ConfigLoader.Load(oldCopy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException)
        { LoadNotice = "默认配置读取或迁移失败：" + ex.Message; }
    }

    private static void LoadPath(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (key.Equals("LastConfigPath", StringComparison.OrdinalIgnoreCase))
                LastConfigPath = value;
        }
    }

    public static void SaveLastConfig(string sourceConfigPath)
    {
        SaveConfig(ConfigLoader.Load(sourceConfigPath));
    }

    public static void SaveConfig(AnalyzerConfig config)
    {
        ConfigLoader.Save(LastConfigCopyPath, config);
        AtomicFile.WriteAllText(
            SettingsPath,
            "[Settings]" + Environment.NewLine + "LastConfigPath=" + LastConfigCopyPath + Environment.NewLine,
            Encoding.UTF8);
        LastConfigPath = LastConfigCopyPath;
    }
}
