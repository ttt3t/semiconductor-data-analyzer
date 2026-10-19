using System.Windows;
using System.Windows.Input;
using System.Globalization;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private int _runIndex = -1;
    private Dictionary<string, int> _runBySerial = new(StringComparer.Ordinal);
    private Dictionary<string, TestItemData> _runItems = new(StringComparer.Ordinal);
    private RunSelectionMode _runSelectionMode;
    private Dictionary<(string Wafer, int X, int Y), int> _runByCoordinates = new();
    private Dictionary<string, List<int>> _runRowsByWafer = new(StringComparer.Ordinal);
    private List<string> _runWafers = new();
    private int[] _runPositions = Array.Empty<int>();
    private bool _updatingRunInput;

    private void ClearCoordinateRunIndex()
    {
        _runByCoordinates = new();
        _runRowsByWafer = new(StringComparer.Ordinal);
        _runWafers = new();
        _runPositions = Array.Empty<int>();
    }

    private void InitializeRun(bool changedData)
    {
        if (changedData)
        {
            // Drop old index capacity too when a large file is replaced by a small one.
            _runBySerial = new(_data.Records.Count, StringComparer.Ordinal);
            _runItems = new(_data.Items.Count, StringComparer.Ordinal);
            ClearCoordinateRunIndex();
            _runPositions = new int[_data.Records.Count];
            Array.Fill(_runPositions, -1);
            for (int row = 0; row < _data.Records.Count; row++)
            {
                _runBySerial.TryAdd(_data.Records[row].SerialNumber.Trim(), row);
                var record = _data.Records[row];
                if (record.X is not int x || record.Y is not int y) continue;
                if (!_runRowsByWafer.TryGetValue(record.WaferId, out var rows))
                {
                    _runRowsByWafer[record.WaferId] = rows = new();
                    _runWafers.Add(record.WaferId);
                }
                _runPositions[row] = rows.Count;
                rows.Add(row);
                _runByCoordinates[(record.WaferId, x, y)] = row;
            }
            foreach (var item in _data.Items) _runItems[item.Id] = item;
            _runIndex = _data.Records.Count == 0 ? -1 : 0;
        }
        EnsureRunForMode();
        if (changedData && _runSelectionMode == RunSelectionMode.WaferCoordinates && _runIndex >= 0)
        {
            var current = _data.Records[_runIndex];
            _runIndex = _runByCoordinates[(current.WaferId, current.X!.Value, current.Y!.Value)];
        }
        UpdateRunInput();
        RefreshRunValues(refreshView: false);
    }

    private void UpdateRunInput()
    {
        _updatingRunInput = true;
        try
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
            var current = available ? _data.Records[_runIndex] : null;
            RunWaferTextBox.Text = current?.WaferId ?? "";
            RunXTextBox.Text = current?.X?.ToString(CultureInfo.InvariantCulture) ?? "";
            RunYTextBox.Text = current?.Y?.ToString(CultureInfo.InvariantCulture) ?? "";
            int waferIndex = current == null ? -1 : _runWafers.IndexOf(current.WaferId);
            PreviousRunWaferButton.IsEnabled = waferIndex > 0;
            NextRunWaferButton.IsEnabled = waferIndex >= 0 && waferIndex + 1 < _runWafers.Count;
            int position = available && _runPositions.Length > _runIndex ? _runPositions[_runIndex] : -1;
            PreviousCoordinateRunButton.IsEnabled = position > 0;
            NextCoordinateRunButton.IsEnabled = position >= 0 && current != null && position + 1 < _runRowsByWafer[current.WaferId].Count;
            RunWaferTextBox.ToolTip = "输入完整晶圆名称后回车，或使用上下按钮切换晶圆；优先保留当前 XY，找不到时显示该晶圆首个坐标的末次记录。";
            RunCoordinatesInput.ToolTip = current == null ? "没有完整 XY 坐标，请在设置中改用序列号模式。" :
                $"{current.WaferId} · ({current.X}, {current.Y}) · Run {current.SerialNumber}\n输入 X/Y 后回车定位末次记录；上下按钮按导入顺序浏览当前晶圆的原始记录（含各次复测）。";
        }
        finally { _updatingRunInput = false; }
    }

    private void EnsureRunForMode()
    {
        if (_runSelectionMode == RunSelectionMode.WaferCoordinates)
        {
            if ((uint)_runIndex >= (uint)_runPositions.Length || _runPositions[_runIndex] < 0)
            {
                _runIndex = _runWafers.Count == 0 ? -1 : _runRowsByWafer[_runWafers[0]][0];
                if (_runIndex >= 0)
                {
                    var current = _data.Records[_runIndex];
                    _runIndex = _runByCoordinates[(current.WaferId, current.X!.Value, current.Y!.Value)];
                }
            }
        }
        else if (_runIndex < 0 && _data.Records.Count > 0) _runIndex = 0;
    }

    private void ApplyRunSelectionMode(RunSelectionMode mode)
    {
        bool changed = _runSelectionMode != mode;
        _runSelectionMode = mode;
        SerialRunSelector.Visibility = mode == RunSelectionMode.SerialNumber ? Visibility.Visible : Visibility.Collapsed;
        CoordinateRunSelector.Visibility = mode == RunSelectionMode.WaferCoordinates ? Visibility.Visible : Visibility.Collapsed;
        if (!changed) return;
        EnsureRunForMode();
        if (mode == RunSelectionMode.WaferCoordinates && _runIndex >= 0)
        {
            var current = _data.Records[_runIndex];
            _runIndex = _runByCoordinates[(current.WaferId, current.X!.Value, current.Y!.Value)];
        }
        UpdateRunInput();
        RefreshRunValues();
        if (mode == RunSelectionMode.WaferCoordinates && _runIndex < 0 && _data.Records.Count > 0)
            TxtStatus.Text = "当前数据没有完整 X/Y 坐标，请在设置中使用序列号模式。";
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
        bool included = record != null && (_sampleIncludedIds == null || _sampleIncludedIds.Contains(record.Id));
        // Read one position from each numeric column; never materialize raw strings or scan every die.
        foreach (var summary in _summaries)
        {
            double? value = null;
            if (included && record != null && (summary.IsCombined || summary.Site == site) &&
                (summary.IsWaferCombined || summary.Wafer == record.WaferId) &&
                _runItems.TryGetValue(summary.Data.Id, out var source))
            {
                double number = source.Values.GetSourceRowValue(_runIndex);
                if (double.IsFinite(number)) value = number * summary.Data.DisplayFactor;
            }
            summary.RunValue = value;
        }
        // Ordinary Run navigation updates only the Value cells, without rebuilding the view or charts.
        if (refreshView && (_columnValueFilters.ContainsKey("RunValue") ||
            _columnFilters.TryGetValue("RunValue", out var filter) && !string.IsNullOrWhiteSpace(filter) ||
            _summaryView?.SortDescriptions.Any(s => s.PropertyName == nameof(TestItemSummary.RunValue)) == true))
            ApplySearchFilter(_currentItem?.DisplayName, _currentSite, _currentWafer);
    }

    private bool CommitRunText()
    {
        if (_updatingRunInput || _runSelectionMode != RunSelectionMode.SerialNumber || _isBusy || _data.Records.Count == 0) return false;
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
    private void PreviousRun_Click(object sender, RoutedEventArgs e) => StepRun(-1);
    private void NextRun_Click(object sender, RoutedEventArgs e) => StepRun(1);

    private void StepRun(int step)
    {
        if (_runSelectionMode == RunSelectionMode.SerialNumber) { SelectRun(_runIndex + step); return; }
        if (!CommitCoordinateRunText()) return;
        var rows = _runRowsByWafer[_data.Records[_runIndex].WaferId];
        int position = _runPositions[_runIndex] + step;
        if ((uint)position < (uint)rows.Count) SelectRun(rows[position]);
    }

    private bool CommitCoordinateRunText()
    {
        if (_updatingRunInput || _isBusy || _runSelectionMode != RunSelectionMode.WaferCoordinates ||
            (uint)_runIndex >= (uint)_data.Records.Count) return false;
        if (int.TryParse(RunXTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(RunYTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) &&
            _runByCoordinates.TryGetValue((RunWaferTextBox.Text.Trim(), x, y), out int row))
        {
            // Leaving the fields unchanged must not jump away from a browsed first retest.
            var current = _data.Records[_runIndex];
            if (current.WaferId != RunWaferTextBox.Text.Trim() || current.X != x || current.Y != y)
                SelectRun(row);
            else UpdateRunInput();
            return true;
        }
        UpdateRunInput();
        TxtStatus.Text = "未找到该晶圆的 X/Y 坐标，已保留原选择。坐标须为整数。";
        return false;
    }

    private bool SelectRunWafer(string wafer)
    {
        if (_isBusy || !_runRowsByWafer.TryGetValue(wafer, out var rows) || rows.Count == 0) return false;
        var current = (uint)_runIndex < (uint)_data.Records.Count ? _data.Records[_runIndex] : null;
        int row = rows[0];
        if (current?.X is int x && current.Y is int y && _runByCoordinates.TryGetValue((wafer, x, y), out int match)) row = match;
        else
        {
            var first = _data.Records[row];
            row = _runByCoordinates[(wafer, first.X!.Value, first.Y!.Value)];
        }
        SelectRun(row);
        return true;
    }

    private bool CommitRunWaferText()
    {
        if (_updatingRunInput || _runSelectionMode != RunSelectionMode.WaferCoordinates || _isBusy ||
            (uint)_runIndex >= (uint)_data.Records.Count) return false;
        string wafer = RunWaferTextBox.Text.Trim();
        if (wafer == _data.Records[_runIndex].WaferId) return true;
        if (SelectRunWafer(wafer)) return true;
        UpdateRunInput();
        TxtStatus.Text = "未找到有完整坐标的晶圆，已保留原选择。";
        return false;
    }

    private void StepRunWafer(int step)
    {
        if (_runIndex < 0 || !CommitRunWaferText()) return;
        int index = _runWafers.IndexOf(_data.Records[_runIndex].WaferId) + step;
        if ((uint)index < (uint)_runWafers.Count) SelectRunWafer(_runWafers[index]);
    }

    private void RunCoordinates_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Enter explicitly locates the last occurrence, even at the currently displayed XY.
            if (CommitCoordinateRunText())
            {
                var current = _data.Records[_runIndex];
                SelectRun(_runByCoordinates[(current.WaferId, current.X!.Value, current.Y!.Value)]);
            }
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down) { StepRun(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateRunInput(); e.Handled = true; }
    }

    private void RunCoordinatesInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && RunCoordinatesInput.IsAncestorOf(next)) return;
        CommitCoordinateRunText();
    }
    private void RunWaferTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRunWaferText(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down) { StepRunWafer(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateRunInput(); e.Handled = true; }
    }
    private void RunWaferTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRunWaferText();
    private void PreviousRunWafer_Click(object sender, RoutedEventArgs e) => StepRunWafer(-1);
    private void NextRunWafer_Click(object sender, RoutedEventArgs e) => StepRunWafer(1);

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
        ShowRetestIdentityNotice(data);
    }

    private void ShowRetestIdentityNotice(CsvParseResult data)
    {
        if (data.RetestIdentityNotice.Length > 0)
            MessageBox.Show(this, data.RetestIdentityNotice, "FT UUID 读取提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
