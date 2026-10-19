using System.IO;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class SettingsWindow
{
    private bool _sbinDescriptionsDirty;
    private void SBinDescriptions_Changed(object sender, TextChangedEventArgs e)
    { if (!_syncing) _sbinDescriptionsDirty = true; }

    private void ShowSBinDescriptions(AnalyzerConfig config)
    {
        bool previous = _syncing; _syncing = true;
        try { TxtSBinDescriptions.Text = string.Join(Environment.NewLine, config.SBinDescriptions.Select(p => p.Key + "=" + p.Value)); _sbinDescriptionsDirty = false; }
        finally { _syncing = previous; }
    }

    private static string ReplaceSBinDescriptions(string ini, string descriptions)
    {
        var body = descriptions.Replace("\r\n", "\n").Split('\n');
        if (body.Any(s => s.TrimStart().StartsWith('['))) throw new InvalidDataException("此页只填写 SBIN=释义，不需要填写节名。");
        var output = new List<string>(); bool skip = false, added = false;
        foreach (string raw in ini.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                skip = line[1..^1].Trim().Equals("SBinDescriptions", StringComparison.OrdinalIgnoreCase);
                if (skip)
                {
                    if (!added) { output.Add("[SBinDescriptions]"); output.AddRange(body); added = true; }
                    continue;
                }
            }
            if (!skip) output.Add(raw);
        }
        if (!added) { output.Add(""); output.Add("[SBinDescriptions]"); output.AddRange(body); }
        return string.Join(Environment.NewLine, output);
    }
}
