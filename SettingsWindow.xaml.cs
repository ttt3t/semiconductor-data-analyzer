using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class SettingsWindow : Window
{
    private readonly Func<AnalyzerConfig, Task> _apply;
    private bool _applying;
    private string _fileName;
    private readonly RecentFilesService _recentFiles;
    private readonly Func<string, Task>? _openCsv;
    private readonly UiPreferencesService _uiPreferences;
    private bool _syncing = true;
    private bool _analysisDirty;
    private TabItem? _lastTab;

    public SettingsWindow(AnalyzerConfig config, Action<AnalyzerConfig> apply)
        : this(config, c => { apply(c); return Task.CompletedTask; }) { }

    public SettingsWindow(AnalyzerConfig config, Func<AnalyzerConfig, Task> apply,
        Func<string, Task>? openCsv = null, RecentFilesService? recentFiles = null,
        UiPreferencesService? uiPreferences = null)
    {
        InitializeComponent();
        _apply = apply;
        _openCsv = openCsv;
        _recentFiles = recentFiles ?? RecentFilesService.Current;
        _uiPreferences = uiPreferences ?? UiPreferencesService.Current;
        _fileName = "config.ini";
        ShowConfig(config);
        ShowPreferences(_uiPreferences.Value);
        _lastTab = SettingsTabs.SelectedItem as TabItem;
        _syncing = false;
        TxtStatus.Text = "分析配置需应用并重算；界面设置可独立保存并立即生效。";
        Closing += (_, e) => { if (_applying) e.Cancel = true; };
        RefreshRecentFiles();
        BtnRecentCsv.IsEnabled = _openCsv != null;
        Closed += (_, _) => { RecentConfigs.ItemsSource = RecentCsvs.ItemsSource = null; };
    }

    private void RefreshRecentFiles()
    {
        RecentConfigs.ItemsSource = new ListCollectionView(_recentFiles.Configs.ToList());
        RecentCsvs.ItemsSource = new ListCollectionView(_recentFiles.Csvs.ToList());
        RecentConfigs.SelectedIndex = RecentConfigs.Items.Count > 0 ? 0 : -1;
        RecentCsvs.SelectedIndex = RecentCsvs.Items.Count > 0 ? 0 : -1;
    }

    private async void BtnRecentConfig_Click(object sender, RoutedEventArgs e)
    {
        if (RecentConfigs.SelectedItem is not RecentFileEntry file) { TxtStatus.Text = "请先选择一个配置文件。"; return; }
        await UseRecentFileAsync(file, config: true);
    }

    private async void BtnRecentCsv_Click(object sender, RoutedEventArgs e)
    {
        if (RecentCsvs.SelectedItem is not RecentFileEntry file) { TxtStatus.Text = "请先选择一个 CSV / STDF 文件。"; return; }
        await UseRecentFileAsync(file, config: false);
    }

    private void RecentConfig_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecentConfigs, source) is ListBoxItem)
        { e.Handled = true; BtnRecentConfig_Click(sender, e); }
    }

    private void RecentCsv_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecentCsvs, source) is ListBoxItem)
        { e.Handled = true; BtnRecentCsv_Click(sender, e); }
    }

    private async Task UseRecentFileAsync(RecentFileEntry file, bool config)
    {
        if (_applying || (!config && _openCsv == null)) return;
        try
        {
            _applying = true; IsEnabled = false; TxtStatus.Text = "处理中…";
            if (config)
            {
                var loaded = ConfigLoader.Load(file.FilePath);
                await _apply(loaded);
                _recentFiles.RememberConfig(file.FilePath);
            }
            else await _openCsv!(file.FilePath);
            _applying = false; DialogResult = true;
        }
        catch (OperationCanceledException) { TxtStatus.Text = "已取消打开，当前数据保持不变。"; }
        catch (Exception ex) { TxtStatus.Text = "打开失败：" + ex.Message; RefreshRecentFiles(); }
        finally { _applying = false; IsEnabled = true; }
    }

    private void ShowConfig(AnalyzerConfig config)
    {
        _fileName = string.IsNullOrEmpty(config.ConfigFileName) ? "config.ini" : Path.ChangeExtension(config.ConfigFileName, ".ini");
        TxtConfig.Text = ConfigLoader.Serialize(config);
        ShowAnalysisControls(config);
    }

    private AnalyzerConfig ReadConfig()
    {
        var config = ConfigLoader.Parse(TxtConfig.Text, _fileName);
        if (!_analysisDirty) return config;
        // The INI remains authoritative; only the controls actually edited in the analysis tab
        // can write it back. Switching from the text editor first reloads all these controls.
        if (!double.TryParse(TxtOutlierSigma.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sigma) || !double.IsFinite(sigma) || sigma <= 0)
            throw new InvalidDataException("离群阈值必须是大于 0 的有限数字，例如 6 或 3.5。");
        if (!int.TryParse(TxtBinCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bins) || bins < 1)
            throw new InvalidDataException("分箱数量必须是大于 0 的整数。");
        string mode = (CmbHistogramMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Fixed";
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RemoveOutliers"] = (ChkRemoveOutliers.IsChecked == true).ToString(),
            ["OutlierSigma"] = sigma.ToString("R", CultureInfo.InvariantCulture),
            ["BinCount"] = bins.ToString(CultureInfo.InvariantCulture), ["BinMode"] = mode,
            ["ShowLowLimit"] = (ChkShowLowLimit.IsChecked == true).ToString(),
            ["ShowHighLimit"] = (ChkShowHighLimit.IsChecked == true).ToString(),
            ["ShowMean"] = (ChkShowMean.IsChecked == true).ToString(),
            ["ShowThreeSigma"] = (ChkShowThreeSigma.IsChecked == true).ToString()
        };
        string edited = UpdateAnalysisFields(TxtConfig.Text, fields);
        config = ConfigLoader.Parse(edited, _fileName);
        TxtConfig.Text = edited;
        _analysisDirty = false;
        return config;
    }

    private static string UpdateAnalysisFields(string text, Dictionary<string, string> fields)
    {
        // Preserve user comments and unrelated configuration when using the convenient controls.
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (section.Equals("SBinCategories", StringComparison.OrdinalIgnoreCase) ||
                section.Equals("ProgramCompatibility", StringComparison.OrdinalIgnoreCase)) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            string key = line[..equals].Trim();
            if (fields.TryGetValue(key, out string? value)) { lines[i] = key + "=" + value; found.Add(key); }
        }
        string result = string.Join(Environment.NewLine, lines);
        var histogram = fields.Where(p => !found.Contains(p.Key) && p.Key is not "RemoveOutliers" and not "OutlierSigma").ToList();
        var robust = fields.Where(p => !found.Contains(p.Key) && p.Key is "RemoveOutliers" or "OutlierSigma").ToList();
        if (histogram.Count > 0) result += Environment.NewLine + "[Histogram]" + Environment.NewLine + string.Join(Environment.NewLine, histogram.Select(p => p.Key + "=" + p.Value));
        if (robust.Count > 0) result += Environment.NewLine + "[Robust]" + Environment.NewLine + string.Join(Environment.NewLine, robust.Select(p => p.Key + "=" + p.Value));
        return result;
    }

    private void ShowAnalysisControls(AnalyzerConfig config)
    {
        bool wasSyncing = _syncing; _syncing = true;
        try
        {
            ChkRemoveOutliers.IsChecked = config.RemoveOutliers;
            TxtOutlierSigma.Text = config.OutlierSigma.ToString("G", CultureInfo.InvariantCulture);
            TxtBinCount.Text = config.BinCount.ToString(CultureInfo.InvariantCulture);
            CmbHistogramMode.SelectedIndex = config.BinMode.ToUpperInvariant() switch
            { "SPEC" or "SPECZONE" => 1, "MEAN" or "MEANCENTERED" or "MEDIAN" or "MEDIANCENTERED" => 2, _ => 0 };
            ChkShowLowLimit.IsChecked = config.ShowLowLimit;
            ChkShowHighLimit.IsChecked = config.ShowHighLimit;
            ChkShowMean.IsChecked = config.ShowMean;
            ChkShowThreeSigma.IsChecked = config.ShowThreeSigma;
            _analysisDirty = false;
        }
        finally { _syncing = wasSyncing; }
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !ReferenceEquals(e.Source, SettingsTabs)) return;
        var selected = SettingsTabs.SelectedItem as TabItem;
        try
        {
            if (ReferenceEquals(_lastTab, AnalysisTab)) _ = ReadConfig();
            if (ReferenceEquals(selected, AnalysisTab)) ShowAnalysisControls(ConfigLoader.Parse(TxtConfig.Text, _fileName));
            _lastTab = selected;
            BtnApplyAnalysis.Visibility = ReferenceEquals(selected, AppearanceTab) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            _syncing = true;
            try { SettingsTabs.SelectedItem = _lastTab; }
            finally { _syncing = false; }
            TxtStatus.Text = "请先修正分析配置：" + ex.Message;
        }
    }

    private void AnalysisControlChanged(object sender, RoutedEventArgs e) { if (!_syncing) _analysisDirty = true; }
    private void AnalysisTextChanged(object sender, TextChangedEventArgs e) { if (!_syncing) _analysisDirty = true; }
    private void AnalysisSelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) _analysisDirty = true; }

    private void ShowPreferences(UiPreferences preferences)
    {
        ChkCompactTable.IsChecked = preferences.CompactTable;
        ChkShowColumnFilters.IsChecked = preferences.ShowColumnFilters;
        ChkAlternateRows.IsChecked = preferences.AlternateRows;
        CmbDefaultStdfMode.SelectedIndex = preferences.DefaultStdfMode == StdfImportMode.Advantest93K ? 1 : 0;
    }

    private void BtnSavePreferences_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _uiPreferences.Save(new UiPreferences
            {
                CompactTable = ChkCompactTable.IsChecked == true,
                ShowColumnFilters = ChkShowColumnFilters.IsChecked == true,
                AlternateRows = ChkAlternateRows.IsChecked == true,
                DefaultStdfMode = CmbDefaultStdfMode.SelectedIndex == 1 ? StdfImportMode.Advantest93K : StdfImportMode.Standard
            });
            TxtStatus.Text = "界面设置已保存并生效；当前数据与分析配置保持不变。";
        }
        catch (Exception ex) { TxtStatus.Text = "界面设置保存失败：" + ex.Message; }
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载 INI 配置",
            Filter = "INI 配置 (*.ini)|*.ini|旧版文本配置 (*.txt)|*.txt",
            DefaultExt = ".ini"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ShowConfig(ConfigLoader.Load(dialog.FileName));
            _recentFiles.RememberConfig(dialog.FileName); RefreshRecentFiles();
            TxtStatus.Text = $"已载入 {Path.GetFileName(dialog.FileName)}，点击“应用并重算”生效。";
        }
        catch (Exception ex) { TxtStatus.Text = "加载失败：" + ex.Message; }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ReadConfig();
            var dialog = new SaveFileDialog
            {
                Title = "导出 INI 配置", Filter = "INI 配置 (*.ini)|*.ini",
                DefaultExt = ".ini", AddExtension = true, FileName = "config.ini"
            };
            if (dialog.ShowDialog(this) != true) return;
            ConfigLoader.Save(dialog.FileName, config);
            _recentFiles.RememberConfig(dialog.FileName); RefreshRecentFiles();
            TxtStatus.Text = "配置已导出：" + dialog.FileName;
        }
        catch (Exception ex) { TxtStatus.Text = "导出失败：" + ex.Message; }
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        try
        {
            var config = ReadConfig();
            _applying = true; IsEnabled = false;
            TxtStatus.Text = "处理中…";
            await _apply(config);
            _applying = false;
            DialogResult = true;
        }
        catch (Exception ex) { TxtStatus.Text = "应用失败：" + ex.Message; }
        finally { _applying = false; IsEnabled = true; }
    }
}
