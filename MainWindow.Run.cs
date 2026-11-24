using System.Windows;
using System.Windows.Input;
using System.Globalization;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private int _runIndex = -1;
    private RunLookup _runLookup = new(0, 0);
    private RunSelectionMode _runSelectionMode;

    private sealed class RunLookup(int records, int items)
    {
        public readonly Dictionary<string, int> BySerial = new(records, StringComparer.Ordinal);
        public readonly Dictionary<string, TestItemData> Items = new(items, StringComparer.Ordinal);
        public readonly Dictionary<(string Lot, string Wafer, int X, int Y), int> ByCoordinates = new();
        public readonly Dictionary<(string Lot, string Wafer), List<int>> RowsByWafer = new();
        public readonly List<(string Lot, string Wafer)> Wafers = new();
        public readonly int[] Positions = new int[records];
        public readonly HashSet<RawMeasurementStore> Stores = new();
        public string[] SBins { get; private set; } = Array.Empty<string>();
        public SampleLocation[] Locations { get; private set; } = Array.Empty<SampleLocation>();

        public static RunLookup Build(CsvParseResult data)
        {
            var lookup = new RunLookup(data.Records.Count, data.Items.Count);
            var rawSBins = new HashSet<string>(StringComparer.Ordinal);
            var locations = new HashSet<SampleLocation>();
            for (int row = 0; row < data.Records.Count; row++)
            {
                if ((row & 1023) == 0) AnalysisOperation.CheckCancellation();
                var record = data.Records[row];
                lookup.BySerial.TryAdd(record.SerialNumber.Trim(), row);
                rawSBins.Add(record.SBin);
                locations.Add(SampleLocation.FromRecord(record));
                if (record.Measurements.RawStore is { } store) lookup.Stores.Add(store);
                lookup.Positions[row] = -1;
                if (record.X is not int x || record.Y is not int y) continue;
                var scope = RunScope(record);
                if (!lookup.RowsByWafer.TryGetValue(scope, out var rows))
                {
                    lookup.RowsByWafer[scope] = rows = new();
                    lookup.Wafers.Add(scope);
                }
                lookup.Positions[row] = rows.Count;
                rows.Add(row);
                lookup.ByCoordinates[(scope.Lot, scope.Wafer, x, y)] = row;
            }
            foreach (var item in data.Items) lookup.Items[item.Id] = item;
            lookup.SBins = rawSBins.Select(BinStatisticsService.NormalizeBin).Where(bin => bin.Length > 0)
                .Distinct(StringComparer.Ordinal).OrderBy(NumericText.SortKey).ThenBy(bin => bin, StringComparer.Ordinal).ToArray();
            lookup.Locations = locations.OrderBy(location => location.Lot, StringComparer.Ordinal)
                .ThenBy(location => location.Wafer, StringComparer.Ordinal).ToArray();
            return lookup;
        }
    }

    private bool _updatingRunInput;
    private static (string Lot, string Wafer) RunScope(CsvRecord row) => (row.Lot.Trim(), row.WaferId);
    private static (string Lot, string Wafer, int X, int Y) RunCoordinateKey(CsvRecord row) =>
        (row.Lot.Trim(), row.WaferId, row.X!.Value, row.Y!.Value);
    private string RunWaferLabel((string Lot, string Wafer) scope) =>
        _runLookup.Wafers.Any(other => other.Wafer == scope.Wafer && other.Lot != scope.Lot)
            ? (scope.Lot.Length > 0 ? scope.Lot : "(未指定 LOT)") + " / " + scope.Wafer : scope.Wafer;

    private bool ResolveRunScope(string text, out (string Lot, string Wafer) scope)
    {
        var candidates = _runLookup.Wafers.Where(s => RunWaferLabel(s) == text).ToArray();
        if (candidates.Length == 1) { scope = candidates[0]; return true; }
        var current = (uint)_runIndex < (uint)_data.Records.Count ? RunScope(_data.Records[_runIndex]) : default;
        if (candidates.Contains(current)) { scope = current; return true; }
        candidates = _runLookup.Wafers.Where(s => s.Wafer == text).ToArray();
        if (candidates.Length == 1) { scope = candidates[0]; return true; }
        if (candidates.Contains(current)) { scope = current; return true; }
        scope = default; return false;
    }

    private static int FirstRun(CsvParseResult source, RunLookup lookup, RunSelectionMode mode)
    {
        if (mode != RunSelectionMode.WaferCoordinates) return source.Records.Count == 0 ? -1 : 0;
        if (lookup.Wafers.Count == 0) return -1;
        var first = source.Records[lookup.RowsByWafer[lookup.Wafers[0]][0]];
        return lookup.ByCoordinates[RunCoordinateKey(first)];
    }

    private void InitializeRun(RunLookup? replacement, int preparedRow)
    {
        if (replacement != null)
        {
            _runLookup = replacement;
            _runIndex = FirstRun(_data, replacement, _runSelectionMode);
        }
        else EnsureRunForMode();
        UpdateRunInput();
        if (_runIndex != preparedRow) RefreshRunValues(refreshView: false);
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
            RunWaferTextBox.Text = current == null ? "" : RunWaferLabel(RunScope(current));
            RunXTextBox.Text = current?.X?.ToString(CultureInfo.InvariantCulture) ?? "";
            RunYTextBox.Text = current?.Y?.ToString(CultureInfo.InvariantCulture) ?? "";
            int waferIndex = current == null ? -1 : _runLookup.Wafers.IndexOf(RunScope(current));
            PreviousRunWaferButton.IsEnabled = waferIndex > 0;
            NextRunWaferButton.IsEnabled = waferIndex >= 0 && waferIndex + 1 < _runLookup.Wafers.Count;
            int position = available && _runLookup.Positions.Length > _runIndex ? _runLookup.Positions[_runIndex] : -1;
            PreviousCoordinateRunButton.IsEnabled = position > 0;
            NextCoordinateRunButton.IsEnabled = position >= 0 && current != null && position + 1 < _runLookup.RowsByWafer[RunScope(current)].Count;
            RunWaferTextBox.ToolTip = "输入晶圆名称后回车，同名跨批次晶圆使用 LOT / 晶圆号；上下按钮切换批次晶圆，优先保留当前 XY，找不到时显示该片首个坐标的末次记录。";
            RunCoordinatesInput.ToolTip = current == null ? "没有完整 XY 坐标，请在设置中改用序列号模式。" :
                $"{RunWaferLabel(RunScope(current))} · ({current.X}, {current.Y}) · Run {current.SerialNumber}\n输入 X/Y 后回车定位同 LOT 晶圆末次记录；上下按钮按导入顺序浏览该 LOT 晶圆的原始记录（含各次复测）。";
        }
        finally { _updatingRunInput = false; }
    }

    private void EnsureRunForMode()
    {
        if (_runSelectionMode == RunSelectionMode.WaferCoordinates)
        {
            if ((uint)_runIndex >= (uint)_runLookup.Positions.Length || _runLookup.Positions[_runIndex] < 0)
            {
                _runIndex = _runLookup.Wafers.Count == 0 ? -1 : _runLookup.RowsByWafer[_runLookup.Wafers[0]][0];
                if (_runIndex >= 0)
                {
                    var current = _data.Records[_runIndex];
                    _runIndex = _runLookup.ByCoordinates[RunCoordinateKey(current)];
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
            _runIndex = _runLookup.ByCoordinates[RunCoordinateKey(current)];
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
        if (changed)
        {
            RefreshRunValues();
            SampleFilterContextChanged?.Invoke();
        }
        var record = _data.Records[index];
        TxtStatus.Text = $"Run {record.SerialNumber} · 第 {index + 1}/{_data.Records.Count} 条 · {record.WaferId} · Site {SiteKey(record.Site)}";
    }

    private void RefreshRunValues(bool refreshView = true)
    {
        SetRunValues(_summaries, _data, _runLookup, _runIndex, _sampleIncludedIds);
        // Ordinary Run navigation updates only the Value cells, without rebuilding the view or charts.
        if (refreshView && (_columnValueFilters.ContainsKey("RunValue") ||
            _columnFilters.TryGetValue("RunValue", out var filter) && filter.IsSearchActive ||
            _summaryView?.SortDescriptions.Any(s => s.PropertyName == nameof(TestItemSummary.RunValue)) == true))
            ApplySearchFilter(_currentItem?.DisplayName, _currentSite, _currentWafer);
    }

    private static void SetRunValues(List<TestItemSummary> summaries, CsvParseResult data, RunLookup lookup,
        int row, HashSet<string>? includedIds, CancellationToken token = default)
    {
        var record = (uint)row < (uint)data.Records.Count ? data.Records[row] : null;
        string site = record == null ? "" : SiteKey(record.Site);
        bool included = record != null && (includedIds == null || includedIds.Contains(record.Id));
        // Read one position from each numeric column; never materialize raw strings or scan every die.
        foreach (var summary in summaries)
        {
            token.ThrowIfCancellationRequested();
            double? value = null;
            if (included && record != null && (summary.IsCombined || summary.Site == site) &&
                (summary.IsWaferCombined || summary.Wafer == record.WaferId) &&
                lookup.Items.TryGetValue(summary.Data.Id, out var source))
            {
                double number = source.Values.GetSourceRowValue(row);
                if (double.IsFinite(number)) value = number * summary.Data.DisplayFactor;
            }
            summary.RunValue = value;
        }
    }

    private bool CommitRunText()
    {
        if (_updatingRunInput || _runSelectionMode != RunSelectionMode.SerialNumber || _isBusy || _data.Records.Count == 0) return false;
        if (_runLookup.BySerial.TryGetValue(RunTextBox.Text.Trim(), out int index))
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
        var rows = _runLookup.RowsByWafer[RunScope(_data.Records[_runIndex])];
        int position = _runLookup.Positions[_runIndex] + step;
        if ((uint)position < (uint)rows.Count) SelectRun(rows[position]);
    }

    private bool CommitCoordinateRunText()
    {
        if (_updatingRunInput || _isBusy || _runSelectionMode != RunSelectionMode.WaferCoordinates ||
            (uint)_runIndex >= (uint)_data.Records.Count) return false;
        if (ResolveRunScope(RunWaferTextBox.Text.Trim(), out var scope) &&
            int.TryParse(RunXTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(RunYTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) &&
            _runLookup.ByCoordinates.TryGetValue((scope.Lot, scope.Wafer, x, y), out int row))
        {
            // Leaving the fields unchanged must not jump away from a browsed first retest.
            var current = _data.Records[_runIndex];
            if (RunScope(current) != scope || current.X != x || current.Y != y)
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
        return ResolveRunScope(wafer, out var scope) && SelectRunScope(scope);
    }
    private bool SelectRunScope((string Lot, string Wafer) scope)
    {
        if (_isBusy || !_runLookup.RowsByWafer.TryGetValue(scope, out var rows) || rows.Count == 0) return false;
        var current = (uint)_runIndex < (uint)_data.Records.Count ? _data.Records[_runIndex] : null;
        int row = rows[0];
        if (current?.X is int x && current.Y is int y && _runLookup.ByCoordinates.TryGetValue((scope.Lot, scope.Wafer, x, y), out int match)) row = match;
        else
        {
            var first = _data.Records[row];
            row = _runLookup.ByCoordinates[(scope.Lot, scope.Wafer, first.X!.Value, first.Y!.Value)];
        }
        SelectRun(row);
        return true;
    }

    private bool CommitRunWaferText()
    {
        if (_updatingRunInput || _runSelectionMode != RunSelectionMode.WaferCoordinates || _isBusy ||
            (uint)_runIndex >= (uint)_data.Records.Count) return false;
        string wafer = RunWaferTextBox.Text.Trim();
        if (wafer == RunWaferLabel(RunScope(_data.Records[_runIndex]))) return true;
        if (SelectRunWafer(wafer)) return true;
        UpdateRunInput();
        TxtStatus.Text = "未找到有完整坐标的晶圆，已保留原选择。";
        return false;
    }

    private void StepRunWafer(int step)
    {
        if (_runIndex < 0 || !CommitRunWaferText()) return;
        int index = _runLookup.Wafers.IndexOf(RunScope(_data.Records[_runIndex])) + step;
        if ((uint)index < (uint)_runLookup.Wafers.Count) SelectRunScope(_runLookup.Wafers[index]);
    }

    private void RunCoordinates_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Enter explicitly locates the last occurrence, even at the currently displayed XY.
            if (CommitCoordinateRunText())
            {
                var current = _data.Records[_runIndex];
                SelectRun(_runLookup.ByCoordinates[RunCoordinateKey(current)]);
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
        RefreshFocusColumnFilter();
        TxtStatus.Text = $"已{(select ? "关注" : "取消关注")}当前筛选内的 {focuses.Length} 个测试项";
    }

    private void ShowSerialNumberNotice(CsvParseResult data)
    {
        if (data.ImportWarning.Length > 0)
            MessageBox.Show(this, data.ImportWarning, "导入警告：已保留可读数据", MessageBoxButton.OK, MessageBoxImage.Warning);
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
