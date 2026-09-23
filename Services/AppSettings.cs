using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class AppSettings
{
    public static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.settings.ini");

    public static string LastConfigCopyPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_config.ini");

    public static string? LastConfigPath { get; private set; }

    public static void Load()
    {
        LastConfigPath = null;
        string path = File.Exists(SettingsPath) ? SettingsPath :
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.settings.txt");
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
        File.WriteAllText(
            SettingsPath,
            "[Settings]" + Environment.NewLine + "LastConfigPath=" + LastConfigCopyPath + Environment.NewLine,
            Encoding.UTF8);
        LastConfigPath = LastConfigCopyPath;
    }
}
