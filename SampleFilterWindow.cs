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
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    public IReadOnlyList<SampleRange> Rules { get; private set; } = Array.Empty<SampleRange>();

    public SampleFilterWindow(IReadOnlyList<TestItemData> items, IReadOnlyList<SampleRange> rules, RetestMode mode)
    {
        items = items.Where(i => i.Focus.IsFocused).ToArray();
        var availableIds = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        int removedCount = rules.Count(r => !availableIds.Contains(r.ItemId));
        rules = rules.Where(r => availableIds.Contains(r.ItemId)).ToArray();
        Title = "数据筛选"; Width = 850; Height = 520; MinWidth = 650; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var note = new TextBlock { Text = (items.Count == 0 ? "请先在主列表勾选关注的测试项。" : "仅列出主列表已关注的测试项。") +
            (removedCount > 0 ? $" {removedCount} 个未关注项的原筛选条件将在应用时移除；返回则保留当前筛选。" : "") +
            "\n添加测试项和闭区间范围，多个条件取交集；留空表示不限。输入单位与主列表一致。\n" +
            (mode != RetestMode.All ? "按当前首测/末测口径匹配（仅复测取末次复测值），允许分段测试；满足条件的芯片保留原始记录。" :
            "按当前口径逐条匹配记录；同条记录必须满足所有条件，缺测和非数值不通过筛选。"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(5, 6, 0, 0) }; b.Click += (_, _) => action(); return b; }
        actions.Children.Add(Button("取消数据筛选", () => { Rules = Array.Empty<SampleRange>(); DialogResult = true; }));
        actions.Children.Add(Button("应用", Apply)); actions.Children.Add(Button("返回", () => DialogResult = false));
        var choose = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(choose, Dock.Top); root.Children.Add(choose);
        var picker = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, DisplayMemberPath = "Name", ItemsSource = items.Select(i => new RangeRow { Item = i }).ToArray(), MaxDropDownHeight = 320, MinWidth = 280, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = items.Count > 0 };
        var add = Button("添加条件", () => { if (picker.SelectedItem is RangeRow row && !_rows!.Any(r => r.Item.Id == row.Item.Id)) { _rows!.Add(new RangeRow { Item = row.Item }); _grid!.Items.Refresh(); } });
        add.IsEnabled = items.Count > 0;
        DockPanel.SetDock(add, Dock.Right); choose.Children.Add(add);
        var remove = Button("移除选中", () => { if (_grid!.SelectedItem is RangeRow row) { _rows!.Remove(row); _grid.Items.Refresh(); } });
        DockPanel.SetDock(remove, Dock.Right); choose.Children.Add(remove); choose.Children.Add(picker);
        _rows = rules.Select(r => new RangeRow { Item = items.First(i => i.Id == r.ItemId),
            Minimum = (r.Minimum * items.First(i => i.Id == r.ItemId).DisplayFactor)?.ToString("G", CultureInfo.InvariantCulture) ?? "",
            Maximum = (r.Maximum * items.First(i => i.Id == r.ItemId).DisplayFactor)?.ToString("G", CultureInfo.InvariantCulture) ?? "" }).ToList();
        _grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = _rows,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Test Item", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "最小值（包含）", Binding = new Binding("Minimum"), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "最大值（包含）", Binding = new Binding("Maximum"), Width = 150 });
        root.Children.Add(_grid);
    }

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
                if (min == null && max == null || min > max) throw new FormatException("至少填写一个边界，且最小值不能大于最大值。");
                rules.Add(new(row.Item.Id, min, max));
            }
            catch (FormatException ex) { _error.Text = row.Item.DisplayName + "：" + ex.Message; return; }
        }
        Rules = rules; DialogResult = true;
    }
}
