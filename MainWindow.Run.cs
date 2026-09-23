using System.Windows;
using System.Windows.Input;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private int _runIndex = -1;
    private Dictionary<string, int> _runBySerial = new(StringComparer.Ordinal);
    private Dictionary<string, TestItemData> _runItems = new(StringComparer.Ordinal);

    private void InitializeRun(bool changedData)
    {
        if (changedData)
        {
            // Drop old index capacity too when a large file is replaced by a small one.
            _runBySerial = new(_data.Records.Count, StringComparer.Ordinal);
            _runItems = new(_data.Items.Count, StringComparer.Ordinal);
            for (int row = 0; row < _data.Records.Count; row++)
                _runBySerial.TryAdd(_data.Records[row].SerialNumber.Trim(), row);
            foreach (var item in _data.Items) _runItems[item.Id] = item;
            _runIndex = _data.Records.Count == 0 ? -1 : 0;
        }
        UpdateRunInput();
        RefreshRunValues(refreshView: false);
    }

    private void UpdateRunInput()
    {
        bool available = (uint)_runIndex < (uint)_data.Records.Count;
        RunSelector.IsEnabled = available;
        PreviousRunButton.IsEnabled = available && _runIndex > 0;
        NextRunButton.IsEnabled = available && _runIndex + 1 < _data.Records.Count;
        RunTextBox.Text = available ? _data.Records[_runIndex].SerialNumber : "";
        if (available)
        {
            var record = _data.Records[_runIndex];
            RunTextBox.ToolTip = $"Run {record.SerialNumber} · 第 {_runIndex + 1} / {_data.Records.Count} 条原始记录\n{record.WaferId} · Site {SiteKey(record.Site)}\n输入序列号后回车；上下键或箭头按导入顺序切换。Value 不受复测统计口径影响。";
        }
        else RunTextBox.ToolTip = "打开数据后，按序列号浏览原始测量值。";
    }

    private void SelectRun(int index)
    {
        if (_isBusy || (uint)index >= (uint)_data.Records.Count) return;
        bool changed = index != _runIndex;
        _runIndex = index;
        UpdateRunInput();
        if (changed) RefreshRunValues();
        var record = _data.Records[index];
        TxtStatus.Text = $"Run {record.SerialNumber} · 第 {index + 1}/{_data.Records.Count} 条 · {record.WaferId} · Site {SiteKey(record.Site)}";
    }

    private void RefreshRunValues(bool refreshView = true)
    {
        var record = (uint)_runIndex < (uint)_data.Records.Count ? _data.Records[_runIndex] : null;
        string site = record == null ? "" : SiteKey(record.Site);
        // Read one position from each numeric column; never materialize raw strings or scan every die.
        foreach (var summary in _summaries)
        {
            double? value = null;
            if (record != null && (summary.IsCombined || summary.Site == site) &&
                _runItems.TryGetValue(summary.Data.Id, out var source))
            {
                double number = source.Values.GetSourceRowValue(_runIndex);
                if (double.IsFinite(number)) value = number;
            }
            summary.RunValue = value;
        }
        // Ordinary Run navigation updates only the Value cells, without rebuilding the view or charts.
        if (refreshView && (_columnValueFilters.ContainsKey("RunValue") ||
            _columnFilters.TryGetValue("RunValue", out var filter) && !string.IsNullOrWhiteSpace(filter) ||
            _summaryView?.SortDescriptions.Any(s => s.PropertyName == nameof(TestItemSummary.RunValue)) == true))
            ApplySearchFilter(_currentItem?.DisplayName, _currentSite);
    }

    private bool CommitRunText()
    {
        if (_isBusy || _data.Records.Count == 0) return false;
        if (_runBySerial.TryGetValue(RunTextBox.Text.Trim(), out int index))
        {
            SelectRun(index);
            return true;
        }
        UpdateRunInput();
        TxtStatus.Text = "未找到该 Run 序列号，已保留原选择。可用上下箭头逐条查看。";
        return false;
    }

    private void RunTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRunText(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        {
            if (CommitRunText()) SelectRun(_runIndex + (e.Key == Key.Up ? 1 : -1));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { UpdateRunInput(); e.Handled = true; }
    }

    private void RunTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRunText();
    private void PreviousRun_Click(object sender, RoutedEventArgs e) => SelectRun(_runIndex - 1);
    private void NextRun_Click(object sender, RoutedEventArgs e) => SelectRun(_runIndex + 1);

    private void ToggleVisibleFocus()
    {
        // One shared focus object per test item, even when it occupies several Site rows.
        var focuses = DgTestItems.Items.OfType<TestItemSummary>().Select(s => s.Data.Focus).Distinct().ToArray();
        if (focuses.Length == 0) return;
        bool select = focuses.Any(f => !f.IsFocused);
        foreach (var focus in focuses) focus.IsFocused = select;
        TxtStatus.Text = $"已{(select ? "关注" : "取消关注")}当前筛选内的 {focuses.Length} 个测试项";
    }

    private void ShowSerialNumberNotice(CsvParseResult data)
    {
        if (!string.IsNullOrWhiteSpace(data.SerialNumberNotice))
            MessageBox.Show(this, data.SerialNumberNotice, "Run 序列号已自动生成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
