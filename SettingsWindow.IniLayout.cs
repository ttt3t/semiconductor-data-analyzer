using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class SettingsWindow
{
    private async void EditIniLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutEditor != null || _applying) return;
        bool applied = false;
        try
        {
            _ = ReadConfig();
            var targets = _layoutSources.Select((source, index) => new IniLayoutWindow.Target(index, source))
                .Where(target => target.Source.ConfigText.Length > 0 && IniLayoutPreview.Supports(target.Source)).ToArray();
            string scope = targets.Length > 0 ? "当前分析 · 所选来源的解析配置" : "数据分析 · 当前 INI 配置";
            _layoutEditor = new IniLayoutWindow(TxtConfig.Text, scope, async (text, targetIndex) =>
            {
                var config = ConfigLoader.Parse(text, _fileName);
                if (_applyLayout != null) await _applyLayout(config, targetIndex);
                else await _apply(config);
                if (targetIndex == null) TxtConfig.Text = text;
            }, _layoutSources.FirstOrDefault(IniLayoutPreview.Supports), targets, _fileName, _cancelApply);
            IsEnabled = false;
            applied = await IndependentWindow.ShowAsync(_layoutEditor);
        }
        catch (Exception ex) { TxtStatus.Text = "无法打开可视化设置：" + ex.Message; }
        finally
        {
            _layoutEditor = null;
            IsEnabled = true;
        }
        if (applied) IndependentWindow.Accept(this);
    }
}
