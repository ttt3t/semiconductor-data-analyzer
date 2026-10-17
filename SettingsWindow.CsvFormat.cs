using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class SettingsWindow
{
    private CancellationTokenSource? _csvDetectionCancellation;
    private bool _closeAfterDetection;
    private string _detectedCsvConfigName = "csv.auto.ini";

    private void BrowseCsvFormat_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Filter = "CSV 文件 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件|*.*", Title = "选择 CSV 格式识别样本" };
        if (file.ShowDialog(this) == true) TxtCsvSamplePath.Text = file.FileName;
    }

    private void CsvSamplePath_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || TxtDetectedCsvIni == null) return;
        TxtDetectedCsvIni.Clear(); TxtCsvFormatSummary.Text = TxtCsvFormatWarnings.Text = "";
        DgCsvFormatPreview.ItemsSource = null;
        TxtStatus.Text = "样本已更换；点击“开始识别”后运行。";
        UpdateCsvFormatButtons();
    }

    private void DetectedCsvIni_Changed(object sender, TextChangedEventArgs e)
    { if (BtnUseCsvFormat != null) UpdateCsvFormatButtons(); }

    private void UpdateCsvFormatButtons()
    {
        bool ready = _csvDetectionCancellation == null && !string.IsNullOrWhiteSpace(TxtDetectedCsvIni.Text);
        BtnUseCsvFormat.IsEnabled = BtnSaveCsvFormat.IsEnabled = BtnPreviewCsvFormat.IsEnabled = ready;
    }

    private async void RecognizeCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_csvDetectionCancellation != null) return;
        try
        {
            string path = TxtCsvSamplePath.Text.Trim();
            var seed = ReadConfig();
            await RunCsvFormatTaskAsync(async token =>
            {
                var result = await Task.Run(() => CsvFormatDetector.Detect(path, seed, token), token);
                token.ThrowIfCancellationRequested();
                _detectedCsvConfigName = result.Config.ConfigFileName;
                TxtDetectedCsvIni.Text = ConfigLoader.Serialize(result.Config);
                TxtCsvFormatSummary.Text = result.Summary;
                TxtCsvFormatWarnings.Text = string.Join(Environment.NewLine, result.Warnings.Select(w => "• " + w));
                ShowCsvSample(result.Rows, result.Config);
                TxtStatus.Text = "识别完成。核对预览和 INI 后可另存，或采用配置再应用；当前分析配置尚未改变。";
            });
        }
        catch (Exception ex) { TxtStatus.Text = "识别失败：" + ex.Message; }
    }

    private async void PreviewCsvFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_csvDetectionCancellation != null) return;
        try
        {
            string path = TxtCsvSamplePath.Text.Trim();
            var config = ConfigLoader.Parse(TxtDetectedCsvIni.Text, _detectedCsvConfigName);
            await RunCsvFormatTaskAsync(async token =>
            {
                var rows = await Task.Run(() => CsvFormatDetector.Preview(path, config, token), token);
                token.ThrowIfCancellationRequested(); ShowCsvSample(rows, config);
                TxtCsvFormatSummary.Text = $"编码 {config.EncodingName} · 名称行 {string.Join(",", CsvText.NameRows(config))} · 数据起始行 {config.DataStartRow} · 测项起始列 {config.TestItemStartColumn}";
                TxtStatus.Text = "已按编辑后的 INI 更新样本预览；未修改当前分析配置。";
            });
        }
        catch (Exception ex) { TxtStatus.Text = "预览失败：" + ex.Message; }
    }

    private async Task RunCsvFormatTaskAsync(Func<CancellationToken, Task> task)
    {
        using var cancellation = new CancellationTokenSource();
        _csvDetectionCancellation = cancellation;
        TxtCsvSamplePath.IsEnabled = BtnBrowseCsvFormat.IsEnabled = BtnRecognizeCsv.IsEnabled = false;
        ConfigActions.IsEnabled = BtnApplyAnalysis.IsEnabled = false;
        foreach (TabItem tab in SettingsTabs.Items) tab.IsEnabled = ReferenceEquals(tab, CsvFormatTab);
        TxtDetectedCsvIni.IsReadOnly = true; BtnCancelCsvDetection.IsEnabled = true;
        BtnSettingsClose.Content = "取消识别"; UpdateCsvFormatButtons(); TxtStatus.Text = "正在采样识别…";
        try { await task(cancellation.Token); }
        catch (OperationCanceledException) { TxtStatus.Text = "已取消格式识别，当前配置保持不变。"; }
        finally
        {
            _csvDetectionCancellation = null;
            TxtCsvSamplePath.IsEnabled = BtnBrowseCsvFormat.IsEnabled = BtnRecognizeCsv.IsEnabled = true;
            ConfigActions.IsEnabled = BtnApplyAnalysis.IsEnabled = true;
            foreach (TabItem tab in SettingsTabs.Items) tab.IsEnabled = true;
            TxtDetectedCsvIni.IsReadOnly = false; BtnCancelCsvDetection.IsEnabled = false;
            BtnSettingsClose.Content = "关闭"; UpdateCsvFormatButtons();
            if (_closeAfterDetection) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void CancelCsvDetection_Click(object sender, RoutedEventArgs e) => _csvDetectionCancellation?.Cancel();

    private void UseCsvFormat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowConfig(ConfigLoader.Parse(TxtDetectedCsvIni.Text, _detectedCsvConfigName));
            SettingsTabs.SelectedItem = IniTab;
            TxtStatus.Text = "识别结果已填入 INI。点击“应用分析配置并重算”后生效，或先导出配置。";
        }
        catch (Exception ex) { TxtStatus.Text = "配置无效：" + ex.Message; }
    }

    private void SaveCsvFormat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ConfigLoader.Parse(TxtDetectedCsvIni.Text, _detectedCsvConfigName);
            var file = new SaveFileDialog { Title = "识别结果 INI 另存为", Filter = "INI 配置 (*.ini)|*.ini", DefaultExt = ".ini",
                AddExtension = true, FileName = _detectedCsvConfigName };
            if (file.ShowDialog(this) != true) return;
            ConfigLoader.Save(file.FileName, config);
            _recentFiles.RememberConfig(file.FileName); RefreshRecentFiles();
            TxtStatus.Text = "识别配置已另存；当前分析配置未改变：" + file.FileName;
        }
        catch (Exception ex) { TxtStatus.Text = "另存失败：" + ex.Message; }
    }

    private void ShowCsvSample(IReadOnlyList<string[]> rows, AnalyzerConfig config)
    {
        int available = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        var measurementColumns = config.TestItemColumns.Count > 0 ? config.TestItemColumns
            : Enumerable.Range(Math.Min(available + 1, config.TestItemStartColumn), Math.Max(0, available - config.TestItemStartColumn + 1)).ToList();
        var columns = measurementColumns.Take(8).Concat(Enumerable.Range(1, available)).Where(c => c > 0 && c <= available).Distinct().Take(16).ToArray();
        int width = columns.Length;
        var table = new DataTable(); table.Columns.Add("记录行", typeof(int));
        foreach (int c in columns) table.Columns.Add("C" + c);
        var indices = Enumerable.Range(0, Math.Min(12, rows.Count))
            .Concat(CsvText.NameRows(config).Concat(new[] { config.LowLimitRow, config.HighLimitRow, config.UnitRow, config.TestNumberRow }).Where(r => r > 0).Select(r => r - 1))
            .Concat(Enumerable.Range(Math.Min(rows.Count, config.DataStartRow - 1), Math.Min(5, Math.Max(0, rows.Count - config.DataStartRow + 1))))
            .Where(r => r >= 0 && r < rows.Count).Distinct().Order().Take(32);
        foreach (int r in indices)
        {
            var values = new object[width + 1]; values[0] = r + 1;
            for (int c = 0; c < width; c++) values[c + 1] = columns[c] <= rows[r].Length ? rows[r][columns[c] - 1] : "";
            table.Rows.Add(values);
        }
        DgCsvFormatPreview.ItemsSource = table.DefaultView;
    }

    private void CsvPreview_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        => e.Column.Width = new DataGridLength(e.PropertyName == "记录行" ? 54 : 104);
}
