using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public sealed class SampleFilterWindow : Window
{
    public sealed class RangeRow
    {
        public TestItemData Item { get; set; } = null!;
        public string Minimum { get; set; } = "";
        public string Maximum { get; set; } = "";
        public string Unit => Item.ShownUnit;
        public string Name => Item.TestNumber + " · " + Item.DisplayName + " [" + Unit + "]";
    }
    private readonly List<RangeRow> _rows;
    private readonly DataGrid _grid;
    private readonly ComboBox _picker;
    private readonly Button _add;
    private readonly RadioButton _union;
    private readonly TextBlock _note;
    private IReadOnlyList<TestItemData> _sourceItems;
    private readonly Func<IReadOnlyList<SampleRange>, SampleMatchMode, Task>? _apply;
    private bool _applying;
    private RetestMode _mode;
    private int _removedConditions;
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    public IReadOnlyList<SampleRange> Rules { get; private set; } = Array.Empty<SampleRange>();
    public SampleMatchMode MatchMode => _union.IsChecked == true ? SampleMatchMode.Union : SampleMatchMode.Intersection;

    public SampleFilterWindow(IReadOnlyList<TestItemData> items, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection, Func<IReadOnlyList<SampleRange>, SampleMatchMode, Task>? apply = null)
    {
        _sourceItems = items; _apply = apply; _mode = mode;
        items = items.Where(i => i.Focus.IsFocused).ToArray();
        var availableIds = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        int removedCount = rules.Count(r => !availableIds.Contains(r.ItemId));
        _removedConditions = removedCount;
        rules = rules.Where(r => availableIds.Contains(r.ItemId)).ToArray();
        Title = "数据筛选"; Width = 850; Height = 520; MinWidth = 650; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_note, Dock.Top); root.Children.Add(_note);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(5, 6, 0, 0) }; b.Click += (_, _) => action(); return b; }
        actions.Children.Add(Button("取消数据筛选", () => _ = SubmitAsync(Array.Empty<SampleRange>())));
        actions.Children.Add(Button("应用", Apply)); actions.Children.Add(Button("关闭", Close));
        var choose = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(choose, Dock.Top); root.Children.Add(choose);
        _picker = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, DisplayMemberPath = "Name", ItemsSource = items.Select(i => new RangeRow { Item = i }).ToArray(), MaxDropDownHeight = 320, MinWidth = 280, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = items.Count > 0 };
        _add = Button("添加条件", () => { if (_picker.SelectedItem is RangeRow row && !_rows!.Any(r => r.Item.Id == row.Item.Id)) { CommitRows(); _rows!.Add(new RangeRow { Item = row.Item }); _grid!.Items.Refresh(); } });
        _add.IsEnabled = items.Count > 0;
        DockPanel.SetDock(_add, Dock.Right); choose.Children.Add(_add);
        var remove = Button("移除选中", () => { if (_grid!.SelectedItem is RangeRow row) { CommitRows(); _rows!.Remove(row); _grid.Items.Refresh(); } });
        DockPanel.SetDock(remove, Dock.Right); choose.Children.Add(remove); choose.Children.Add(_picker);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(modes, Dock.Top); root.Children.Add(modes);
        modes.Children.Add(new TextBlock { Text = "测项条件：", VerticalAlignment = VerticalAlignment.Center });
        var intersection = new RadioButton { Content = "交集（全部满足）", GroupName = "FilterMatch", Margin = new Thickness(8, 0, 18, 0), IsChecked = match == SampleMatchMode.Intersection };
        _union = new RadioButton { Content = "并集（任一满足）", GroupName = "FilterMatch", IsChecked = match == SampleMatchMode.Union };
        modes.Children.Add(intersection); modes.Children.Add(_union);
        intersection.Checked += (_, _) => UpdateNote(); _union.Checked += (_, _) => UpdateNote();
        _rows = rules.Select(r => new RangeRow { Item = items.First(i => i.Id == r.ItemId),
            Minimum = (r.Minimum * items.First(i => i.Id == r.ItemId).DisplayFactor)?.ToString("G", CultureInfo.InvariantCulture) ?? "",
            Maximum = (r.Maximum * items.First(i => i.Id == r.ItemId).DisplayFactor)?.ToString("G", CultureInfo.InvariantCulture) ?? "" }).ToList();
        _grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = _rows,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Test Item", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "小于等于 ≤", Binding = new Binding("Maximum"), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "大于等于 ≥", Binding = new Binding("Minimum"), Width = 150 });
        root.Children.Add(_grid);
        UpdateNote();
        Activated += (_, _) => RefreshChoices();
        Closing += (_, e) => { if (_applying) e.Cancel = true; };
    }

    public void UpdateItems(IReadOnlyList<TestItemData> items, RetestMode mode)
    {
        _mode = mode;
        if (!ReferenceEquals(items, _sourceItems)) { CommitRows(); _rows.Clear(); _removedConditions = 0; _sourceItems = items; _error.Text = "数据已更新，请重新添加筛选条件。"; }
        RefreshChoices();
    }

    private void UpdateNote()
    {
        if (_note == null || _union == null) return;
        _note.Text = (_sourceItems.Any(i => i.Focus.IsFocused) ? "仅列出主列表已关注的测试项。" : "请先在主列表勾选关注的测试项。") +
            (_removedConditions > 0 ? $" {_removedConditions} 个未关注项条件将在应用时移除；关闭保留当前筛选。" : "") +
            "\n≤ 小值、≥ 大值选两侧；≤ 大值、≥ 小值选中间。边界包含；留空表示单侧不限。\n" +
            (_mode == RetestMode.All ? "按原始记录匹配；" : "按当前复测口径匹配芯片，支持分段测试（仅复测取末次复测值）；") +
            (MatchMode == SampleMatchMode.Union ? "满足任一测项条件即保留。" : "必须满足全部测项条件。") + "缺测/无效值不满足对应条件。";
    }

    private void RefreshChoices()
    {
        if (_applying) return;
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var focused = _sourceItems.Where(i => i.Focus.IsFocused).ToArray();
        string? selected = (_picker.SelectedItem as RangeRow)?.Item.Id;
        _picker.ItemsSource = focused.Select(i => new RangeRow { Item = i }).ToArray();
        _picker.SelectedItem = _picker.Items.OfType<RangeRow>().FirstOrDefault(r => r.Item.Id == selected);
        _picker.IsEnabled = _add.IsEnabled = focused.Length > 0;
        int removed = _rows.RemoveAll(r => !focused.Any(i => i.Id == r.Item.Id));
        _removedConditions += removed; UpdateNote();
        if (removed > 0) _error.Text = "已移除未关注项的待编辑条件；点击应用后生效。";
        _grid.Items.Refresh();
    }

    private void CommitRows()
    { _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true); }

    private void Apply()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var rules = new List<SampleRange>();
        foreach (var row in _rows)
        {
            double? Parse(string text) => text.Trim().Length == 0 ? null : DatasetService.TryValue(text, out double value)
                ? value / row.Item.DisplayFactor : throw new FormatException("范围必须是有限数字。");
            try
            {
                double? min = Parse(row.Minimum), max = Parse(row.Maximum);
                if (min == null && max == null) throw new FormatException("至少填写一个边界。");
                rules.Add(new(row.Item.Id, min, max));
            }
            catch (FormatException ex) { _error.Text = row.Item.DisplayName + "：" + ex.Message; return; }
        }
        _ = SubmitAsync(rules);
    }

    private async Task SubmitAsync(IReadOnlyList<SampleRange> rules)
    {
        if (_applying) return;
        _applying = true; IsEnabled = false;
        try
        {
            if (_apply != null) await _apply(rules, MatchMode);
            Rules = rules;
            if (rules.Count == 0) { _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true); _rows.Clear(); _grid.Items.Refresh(); }
            _error.Text = rules.Count == 0 ? "已取消数据筛选。" : "已应用，可以继续操作主窗口或修改条件。";
            _removedConditions = 0; UpdateNote();
        }
        catch (OperationCanceledException) { _error.Text = "已取消计算，保留原筛选。"; }
        catch (Exception ex) { _error.Text = ex.Message; }
        finally { _applying = false; IsEnabled = true; }
    }
}
