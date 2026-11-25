using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public sealed class SampleFilterWindow : Window
{
    public class RangeInput : INotifyPropertyChanged
    {
        private readonly (string Lower, string Upper)[] _inputs = [("", ""), ("", ""), ("", ""), ("", "")];
        private SampleCalculation _calculation;
        private SampleBaseline _baseline;
        private string _reference = "";
        public SampleCalculation Calculation { get => _calculation; set => Set(ref _calculation, value); }
        public SampleBaseline Baseline
        {
            get => Calculation == SampleCalculation.StandardDeviation ? SampleBaseline.Mean : _baseline;
            set { if (UsesSelectableBaseline) Set(ref _baseline, value); }
        }
        public string Minimum { get => _inputs[(int)Calculation].Lower; set => Set(ref _inputs[(int)Calculation].Lower, value); }
        public string Maximum { get => _inputs[(int)Calculation].Upper; set => Set(ref _inputs[(int)Calculation].Upper, value); }
        public string Reference { get => _reference; set => Set(ref _reference, value); }
        public bool UsesDeviation => Calculation != SampleCalculation.Bounds;
        public bool UsesUpper => Calculation != SampleCalculation.StandardDeviation;
        public bool UsesSelectableBaseline => UsesDeviation && UsesUpper;
        public bool UsesCustom => UsesSelectableBaseline && Baseline == SampleBaseline.Custom;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

        internal void CopyFrom(RangeInput source)
        {
            _calculation = source._calculation;
            _baseline = source._baseline;
            _reference = source._reference;
            source._inputs.CopyTo(_inputs, 0);
            Notify("");
        }

        internal void FillFrom(RangeInput source)
        {
            _calculation = source._calculation;
            if (UsesSelectableBaseline) _baseline = source._baseline;
            if (UsesCustom) _reference = source._reference;
            _inputs[(int)Calculation] = source._inputs[(int)Calculation];
            Changed();
        }

        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Changed();
        }

        private void Changed() { OnEdited(); Notify(""); }
        protected virtual void OnEdited() { }
    }

    public sealed class RangeRow : RangeInput
    {
        private string _preview = "待计算";
        public required TestItemData Item { get; init; }
        public string Name => Item.TestNumber + " · " + Item.DisplayName + " [" + Item.ShownUnit + "]";
        public string Preview
        {
            get => _preview;
            set { if (_preview == value) return; _preview = value; Notify(nameof(Preview)); }
        }
        internal SampleDeviation? SavedGolden { get; init; }
        protected override void OnEdited() => Preview = "待计算";

        internal RangeRow Copy()
        {
            var row = new RangeRow { Item = Item, SavedGolden = SavedGolden };
            row.CopyFrom(this);
            return row;
        }
    }

    private static readonly Dictionary<SampleCalculation, string> Calculations = new()
    {
        [SampleCalculation.Bounds] = "绝对范围", [SampleCalculation.Relative] = "相对偏差 (%)",
        [SampleCalculation.Absolute] = "绝对偏差", [SampleCalculation.StandardDeviation] = "标准差偏差"
    };
    private static readonly Dictionary<SampleBaseline, string> Baselines = new()
    {
        [SampleBaseline.Custom] = "自定义基准", [SampleBaseline.Mean] = "数据均值", [SampleBaseline.Median] = "数据中位数",
        [SampleBaseline.SpecCenter] = "Spec 中心值", [SampleBaseline.Golden] = "Golden 样品", [SampleBaseline.WaferMean] = "Wafer 均值"
    };
    private sealed record LocationChoice(SampleLocation Location)
    {
        public string Label => (Location.Lot.Length == 0 ? "未指定批次" : Location.Lot) +
            (Location.Wafer == null ? "" : " / " + (Location.Wafer.Length == 0 ? "未指定晶圆" : Location.Wafer));
    }
    internal sealed record Draft(Dictionary<string, RangeRow> Rows, RangeInput Batch, SampleMatchMode Match, bool Outside,
        string[] SBins, SampleLocation[] Locations, CsvRecord? GoldenRecord, string? GoldenLabel);

    private readonly List<RangeRow> _rows = new();
    private readonly Dictionary<string, RangeRow> _drafts = new(StringComparer.Ordinal);
    private readonly RangeInput _batch = new();
    private readonly DataGrid _grid;
    private readonly ListBox _sBinList, _lotList, _waferList;
    private readonly RadioButton _intersection, _union, _inside, _outside;
    private readonly TextBlock _note, _goldenNote;
    private readonly IReadOnlyList<TestItemData> _sourceItems;
    private readonly Func<IReadOnlyList<SampleRange>, SampleMatchMode, IReadOnlyList<string>, IReadOnlyList<SampleLocation>, Task>? _apply;
    private readonly Func<IReadOnlyList<SampleRange>, CancellationToken, Task<Dictionary<string, string>>>? _preview;
    private readonly Func<CsvRecord?>? _currentRun;
    private CancellationTokenSource? _previewCancellation;
    private CsvRecord? _goldenRecord;
    private string? _goldenLabel;
    private bool _applying, _closed;
    private readonly RetestMode _mode;
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    public IReadOnlyList<SampleRange> Rules { get; private set; }
    internal event Action<Draft?>? DraftSaved;
    public SampleMatchMode MatchMode => _union.IsChecked == true ? SampleMatchMode.Union : SampleMatchMode.Intersection;
    private bool Outside => _outside.IsChecked == true;

    public SampleFilterWindow(IReadOnlyList<TestItemData> items, IReadOnlyList<SampleRange> rules, RetestMode mode,
        SampleMatchMode match = SampleMatchMode.Intersection,
        Func<IReadOnlyList<SampleRange>, SampleMatchMode, IReadOnlyList<string>, IReadOnlyList<SampleLocation>, Task>? apply = null,
        IReadOnlyList<string>? sBins = null, IReadOnlyList<string>? selectedSBins = null,
        IReadOnlyList<SampleLocation>? locations = null, IReadOnlyList<SampleLocation>? selectedLocations = null,
        Func<CsvRecord?>? currentRun = null,
        Func<IReadOnlyList<SampleRange>, CancellationToken, Task<Dictionary<string, string>>>? preview = null)
    {
        _sourceItems = items; _apply = apply; _mode = mode; _currentRun = currentRun; _preview = preview;
        Rules = rules;
        var prior = rules.ToDictionary(r => r.ItemId, StringComparer.Ordinal);
        foreach (var item in items)
            if (prior.TryGetValue(item.Id, out var rule)) _drafts[item.Id] = MakeRow(item, rule);

        Title = "数据筛选"; Width = 1130; Height = 590; MinWidth = 650; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(5, 6, 0, 0) }; b.Click += (_, _) => action(); return b; }
        actions.Children.Add(Button("取消数据筛选", CancelFilter));
        actions.Children.Add(Button("应用", Apply)); actions.Children.Add(Button("关闭", Close));

        var description = new TextBlock { Text = "测项范围、批次／晶圆、SBIN 取交集，同时满足所有已设置的页面才保留。空白页不限制结果，三页都空时保留全部。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(description, Dock.Top); root.Children.Add(description);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var rangesPanel = new DockPanel { Margin = new Thickness(12) };
        tabs.Items.Add(new TabItem { Header = "测项范围", Content = rangesPanel });
        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_note, Dock.Top); rangesPanel.Children.Add(_note);
        var modes = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(modes, Dock.Top); rangesPanel.Children.Add(modes);
        modes.Children.Add(new TextBlock { Text = "测项组合", VerticalAlignment = VerticalAlignment.Center });
        RadioButton Radio(string text, string group, bool active)
        {
            var button = new RadioButton { Content = text, GroupName = group, Margin = new Thickness(8, 2, 12, 2), IsChecked = active };
            modes.Children.Add(button);
            return button;
        }
        _intersection = Radio("交集 AND", "FilterMatch", match == SampleMatchMode.Intersection);
        _union = Radio("并集 OR", "FilterMatch", match == SampleMatchMode.Union);
        bool outside = rules.FirstOrDefault()?.Outside == true;
        _inside = Radio("范围内", "FilterSide", !outside);
        _outside = Radio("范围外", "FilterSide", outside);
        _inside.Checked += (_, _) => InvalidatePreviews();
        _outside.Checked += (_, _) => InvalidatePreviews();
        var previewButton = Button("计算预览", () => _ = UpdatePreviewsAsync());
        previewButton.Margin = new Thickness(8, 0, 0, 0);
        previewButton.IsEnabled = preview != null;
        modes.Children.Add(previewButton);

        var golden = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(golden, Dock.Top); rangesPanel.Children.Add(golden);
        var setGolden = Button("将当前 Run 设为 Golden", SetGolden);
        setGolden.Margin = new Thickness(0, 0, 10, 0);
        setGolden.IsEnabled = currentRun != null;
        DockPanel.SetDock(setGolden, Dock.Left); golden.Children.Add(setGolden);
        string[] savedGolden = rules.Select(r => r.Deviation?.GoldenLabel).OfType<string>().Distinct().ToArray();
        _goldenNote = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Text = savedGolden.Length == 1 ? "Golden：" + savedGolden[0] : "Golden：从主窗口选定 Run 后设置" };
        golden.Children.Add(_goldenNote);

        var batch = new WrapPanel { DataContext = _batch, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(batch, Dock.Top); rangesPanel.Children.Add(batch);
        void BatchField(string label, Control input, string? enabled = null)
        {
            var field = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
            field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
            if (enabled != null) input.SetBinding(IsEnabledProperty, new Binding(enabled));
            field.Children.Add(input); batch.Children.Add(field);
        }
        ComboBox BatchCombo(System.Collections.IEnumerable options, string property, int width)
        {
            var combo = new ComboBox { ItemsSource = options, SelectedValuePath = "Key", DisplayMemberPath = "Value", Width = width };
            combo.SetBinding(Selector.SelectedValueProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            return combo;
        }
        TextBox BatchText(string property, int width)
        {
            var text = new TextBox { Width = width, Padding = new Thickness(3, 2, 3, 2) };
            text.SetBinding(TextBox.TextProperty, new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return text;
        }
        BatchField("批量计算方式", BatchCombo(Calculations, nameof(RangeInput.Calculation), 126));
        BatchField("基准来源", BatchCombo(Baselines, nameof(RangeInput.Baseline), 128), nameof(RangeInput.UsesSelectableBaseline));
        BatchField("自定义基准值", BatchText(nameof(RangeInput.Reference), 105), nameof(RangeInput.UsesCustom));
        BatchField("下限 / 负偏差 / σ 倍数", BatchText(nameof(RangeInput.Minimum), 145));
        BatchField("上限 / 正偏差", BatchText(nameof(RangeInput.Maximum), 109), nameof(RangeInput.UsesUpper));
        var fill = Button("填入全部测项", FillRanges);
        fill.VerticalAlignment = VerticalAlignment.Bottom;
        fill.Margin = new Thickness(0, 0, 0, 4);
        batch.Children.Add(fill);

        _grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = _rows,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Test Item", Binding = new Binding(nameof(RangeRow.Name)), IsReadOnly = true,
            MinWidth = 170, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        void Combo(string header, string property, System.Collections.IEnumerable options, int width, string? enabled = null)
        {
            var col = new DataGridComboBoxColumn { Header = header, ItemsSource = options,
                SelectedValueBinding = new Binding(property) { Mode = BindingMode.TwoWay }, SelectedValuePath = "Key", DisplayMemberPath = "Value", Width = width };
            if (enabled != null) col.CellStyle = EnabledCell(enabled);
            _grid.Columns.Add(col);
        }
        void TextColumn(string header, string property, int width, string? enabled = null, bool readOnly = false)
        {
            var col = new DataGridTextColumn { Header = header, Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = width, IsReadOnly = readOnly };
            if (enabled != null) col.CellStyle = EnabledCell(enabled);
            if (readOnly)
            {
                var style = new Style(typeof(TextBlock), col.ElementStyle);
                style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
                col.ElementStyle = style;
            }
            _grid.Columns.Add(col);
        }
        Combo("计算方式", nameof(RangeRow.Calculation), Calculations, 126);
        Combo("基准来源", nameof(RangeRow.Baseline), Baselines, 128, nameof(RangeRow.UsesSelectableBaseline));
        TextColumn("自定义基准值", nameof(RangeRow.Reference), 105, nameof(RangeRow.UsesCustom));
        TextColumn("下限 / 负偏差 / σ 倍数", nameof(RangeRow.Minimum), 145);
        TextColumn("上限 / 正偏差", nameof(RangeRow.Maximum), 109, nameof(RangeRow.UsesUpper));
        TextColumn("计算范围", nameof(RangeRow.Preview), 225, readOnly: true);
        rangesPanel.Children.Add(_grid);

        var binsPanel = new DockPanel { Margin = new Thickness(12) };
        tabs.Items.Add(new TabItem { Header = "SBIN", Content = binsPanel });
        var binsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = sBins is { Count: > 0 }
                ? "可同时勾选多个 SBIN，保留命中任一所选 SBIN 的记录。"
                : "当前数据没有可筛选的 SBIN。" };
        DockPanel.SetDock(binsNote, Dock.Top); binsPanel.Children.Add(binsNote);
        _sBinList = SelectionList(sBins ?? Array.Empty<string>());
        foreach (string bin in selectedSBins ?? Array.Empty<string>()) _sBinList.SelectedItems.Add(bin);
        binsPanel.Children.Add(_sBinList);

        var locationsPanel = new DockPanel { Margin = new Thickness(12) };
        tabs.Items.Add(new TabItem { Header = "批次／晶圆", Content = locationsPanel });
        var locationsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "可勾选整批，也可只勾选指定晶圆。选中整批会保留该批的全部记录。同名晶圆按批次区分。" };
        DockPanel.SetDock(locationsNote, Dock.Top); locationsPanel.Children.Add(locationsNote);
        var lists = new Grid();
        lists.ColumnDefinitions.Add(new());
        lists.ColumnDefinitions.Add(new());
        var wafers = locations ?? Array.Empty<SampleLocation>();
        var lots = wafers.Select(location => new SampleLocation(location.Lot)).Distinct();
        var savedLocations = selectedLocations?.ToHashSet() ?? new HashSet<SampleLocation>();
        ListBox AddLocations(string name, IEnumerable<SampleLocation> choices, int column)
        {
            var panel = new DockPanel { Margin = column == 0 ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0) };
            var header = new TextBlock { Text = name, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
            var entries = choices.Select(location => new LocationChoice(location)).ToArray();
            var list = SelectionList(entries, nameof(LocationChoice.Label));
            foreach (var entry in entries)
                if (savedLocations.Contains(entry.Location)) list.SelectedItems.Add(entry);
            panel.Children.Add(list);
            Grid.SetColumn(panel, column); lists.Children.Add(panel);
            return list;
        }
        _lotList = AddLocations("批次", lots, 0);
        _waferList = AddLocations("批次 / 晶圆", wafers, 1);
        locationsPanel.Children.Add(lists);
        tabs.SelectedIndex = 0;
        RefreshFocusedRows();
        Closing += (_, e) => { if (_applying) e.Cancel = true; };
        Closed += (_, _) => { _closed = true; _previewCancellation?.Cancel(); };
    }

    private static ListBox SelectionList(System.Collections.IEnumerable items, string? displayProperty = null)
    {
        var checkBox = new FrameworkElementFactory(typeof(CheckBox));
        checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
        checkBox.SetBinding(ContentControl.ContentProperty, displayProperty == null ? new Binding() : new Binding(displayProperty));
        checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1),
            Mode = BindingMode.TwoWay
        });
        return new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = items,
            ItemTemplate = new DataTemplate { VisualTree = checkBox } };
    }

    private static Style EnabledCell(string property)
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(IsEnabledProperty, new Binding(property)));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .5));
        style.Triggers.Add(disabled);
        return style;
    }

    private RangeRow MakeRow(TestItemData item, SampleRange? rule)
    {
        string Number(double? value, double scale) => (value * scale)?.ToString("G", CultureInfo.InvariantCulture) ?? "";
        var row = new RangeRow { Item = item, SavedGolden = rule?.Deviation is { Baseline: SampleBaseline.Golden } saved ? saved : null };
        if (rule?.Deviation is { } deviation)
        {
            row.Calculation = deviation.Calculation;
            row.Baseline = deviation.Baseline;
            var scale = deviation.Calculation is SampleCalculation.Relative or SampleCalculation.StandardDeviation ? 1 : item.DisplayFactor;
            row.Minimum = Number(deviation.Negative, scale);
            if (row.UsesUpper) row.Maximum = Number(deviation.Positive, scale);
            if (deviation.Baseline == SampleBaseline.Custom) row.Reference = Number(deviation.Reference, item.DisplayFactor);
        }
        else
        {
            row.Minimum = Number(rule?.Minimum, item.DisplayFactor);
            row.Maximum = Number(rule?.Maximum, item.DisplayFactor);
        }
        return row;
    }

    internal void RefreshFocusedRows()
    {
        if (_applying || _previewCancellation != null || _closed) return;
        var focused = _sourceItems.Where(i => i.Focus.IsFocused).ToArray();
        if (!_rows.Select(row => row.Item.Id).SequenceEqual(focused.Select(item => item.Id)))
        {
            CommitRows();
            _rows.Clear();
            foreach (var item in focused)
            {
                if (!_drafts.TryGetValue(item.Id, out var row)) _drafts[item.Id] = row = MakeRow(item, null);
                _rows.Add(row);
            }
            _grid.Items.Refresh();
        }
        UpdateNote();
    }

    private void UpdateNote()
    {
        var visible = _rows.Select(row => row.Item.Id).ToHashSet(StringComparer.Ordinal);
        int removed = Rules.Count(rule => !visible.Contains(rule.ItemId));
        _note.Text = (_rows.Count == 0 ? "在主窗口勾选「关注」，测项会自动加入表格。" : "已关注的测项自动加入。两侧都留空时，该测项暂停参与筛选。") +
            (removed == 0 ? "" : $" 已取消关注的 {removed} 个条件将在应用时移除。") +
            "\n范围内包含边界，范围外排除边界。两侧偏差可分别填写；缺测或无效值不匹配。" +
            (_mode == RetestMode.All ? " 当前逐条匹配原始记录。" : " 当前按复测口径匹配芯片。");
    }

    private void SetGolden()
    {
        CommitRows();
        if (_currentRun?.Invoke() is not { } record)
        {
            _error.Text = "请先在对应的主窗口选择一个有效 Run。";
            return;
        }
        _goldenRecord = record;
        _goldenLabel = string.Join(" / ", new[] { record.Lot.Trim(), record.WaferId, "Run " + record.SerialNumber }.Where(s => s.Length > 0));
        _goldenNote.Text = "Golden：" + _goldenLabel;
        _error.Text = "";
        foreach (var row in _drafts.Values)
            if (row.Baseline == SampleBaseline.Golden) row.Preview = "待计算";
    }

    private void CommitRows()
    { _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true); }

    private void FillRanges()
    {
        CommitRows();
        foreach (var row in _rows) row.FillFrom(_batch);
        _error.Text = _rows.Count == 0 ? "请先在主窗口勾选要填写的测项。" : $"已填入 {_rows.Count} 个测项。";
    }

    internal void RestoreDraft(Draft draft)
    {
        _rows.Clear();
        _drafts.Clear();
        foreach (var (id, row) in draft.Rows) _drafts.Add(id, row.Copy());
        _batch.CopyFrom(draft.Batch);
        (draft.Match == SampleMatchMode.Union ? _union : _intersection).IsChecked = true;
        (draft.Outside ? _outside : _inside).IsChecked = true;
        _goldenRecord = draft.GoldenRecord;
        _goldenLabel = draft.GoldenLabel;
        var savedGolden = _drafts.Values.Select(row => row.SavedGolden?.GoldenLabel).OfType<string>().Distinct().ToArray();
        if ((_goldenLabel ?? (savedGolden.Length == 1 ? savedGolden[0] : null)) is { } label)
            _goldenNote.Text = "Golden：" + label;
        foreach (string bin in draft.SBins) _sBinList.SelectedItems.Add(bin);
        var locations = draft.Locations.ToHashSet();
        foreach (var list in new[] { _lotList, _waferList })
            foreach (LocationChoice entry in list.Items)
                if (locations.Contains(entry.Location)) list.SelectedItems.Add(entry);
        RefreshFocusedRows();
    }

    private Draft CaptureDraft()
    {
        var batch = new RangeInput();
        batch.CopyFrom(_batch);
        return new Draft(_drafts.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal),
            batch, MatchMode, Outside, SelectedSBins(), SelectedLocations(), _goldenRecord, _goldenLabel);
    }

    private SampleRange? MakeRule(RangeRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Minimum) && (!row.UsesUpper || string.IsNullOrWhiteSpace(row.Maximum))) return null;
        double? Parse(string text, double divisor) => string.IsNullOrWhiteSpace(text) ? null :
            DatasetService.TryValue(text, out double value) && double.IsFinite(value / divisor)
                ? value / divisor : throw new FormatException("请输入有限数字。");
        double scale = row.Calculation is SampleCalculation.Relative or SampleCalculation.StandardDeviation ? 1 : row.Item.DisplayFactor;
        var min = Parse(row.Minimum, scale);
        var max = row.UsesUpper ? Parse(row.Maximum, scale) : null;
        if (!row.UsesDeviation) return new(row.Item.Id, min, max) { Outside = Outside };
        if (min < 0 || max < 0) throw new FormatException(row.UsesUpper
            ? "负偏差和正偏差请填写非负的幅度。" : "标准差倍数请填写非负的数值。");
        double? reference = null;
        string? goldenLabel = null;
        if (row.Baseline == SampleBaseline.Custom)
            reference = Parse(row.Reference, row.Item.DisplayFactor) ?? throw new FormatException("请填写自定义基准值。");
        else if (row.Baseline == SampleBaseline.Golden)
        {
            reference = _goldenRecord == null ? row.SavedGolden?.Reference : SampleFilterService.ReadValue(_goldenRecord, row.Item.Id);
            if (!reference.HasValue) throw new FormatException("请用含该测项有效测量值的 Run 设置 Golden。");
            goldenLabel = _goldenLabel ?? row.SavedGolden?.GoldenLabel;
        }
        return new(row.Item.Id, null, null)
        {
            Outside = Outside, Deviation = new(row.Calculation, row.Baseline, min, max, reference, goldenLabel)
        };
    }

    private string[] SelectedSBins() => _sBinList.SelectedItems.Cast<string>().Order(StringComparer.Ordinal).ToArray();
    private SampleLocation[] SelectedLocations() => _lotList.SelectedItems.Cast<LocationChoice>()
        .Concat(_waferList.SelectedItems.Cast<LocationChoice>())
        .Select(entry => entry.Location).OrderBy(location => location.Lot, StringComparer.Ordinal)
        .ThenBy(location => location.Wafer, StringComparer.Ordinal).ToArray();

    private void InvalidatePreviews()
    {
        foreach (var row in _drafts.Values) row.Preview = "待计算";
    }

    private async Task UpdatePreviewsAsync()
    {
        if (_closed || _applying || _previewCancellation != null || _preview == null) return;
        CommitRows();
        _error.Text = "";
        var rules = new List<SampleRange>();
        foreach (var row in _rows)
        {
            try
            {
                if (MakeRule(row) is { } rule) { rules.Add(rule); row.Preview = "正在计算…"; }
                else row.Preview = "填写后预览";
            }
            catch (FormatException ex) { row.Preview = ex.Message; }
        }
        if (rules.Count == 0) return;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        IsEnabled = false;
        var token = cancellation.Token;
        try
        {
            var result = await _preview(rules, token);
            if (!token.IsCancellationRequested && !_closed)
                foreach (var row in _rows)
                    if (result.TryGetValue(row.Item.Id, out var text)) row.Preview = text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_closed)
            {
                InvalidatePreviews();
                _error.Text = "范围预览：" + ex.Message;
            }
        }
        finally
        {
            _previewCancellation = null;
            if (!_closed) { IsEnabled = true; RefreshFocusedRows(); }
        }
    }

    private void Apply()
    {
        CommitRows();
        var rules = new List<SampleRange>();
        foreach (var row in _rows)
        {
            try { if (MakeRule(row) is { } rule) rules.Add(rule); }
            catch (FormatException ex) { _error.Text = row.Item.DisplayName + "：" + ex.Message; return; }
        }
        _ = SubmitAsync(rules, SelectedSBins(), SelectedLocations());
    }

    private void CancelFilter()
    {
        CommitRows();
        _ = SubmitAsync(Array.Empty<SampleRange>(), Array.Empty<string>(), Array.Empty<SampleLocation>(), preserveDraft: true);
    }

    private async Task SubmitAsync(IReadOnlyList<SampleRange> rules, IReadOnlyList<string> sBins,
        IReadOnlyList<SampleLocation> locations, bool preserveDraft = false)
    {
        if (_applying || _previewCancellation != null) return;
        _applying = true; IsEnabled = false;
        try
        {
            if (_apply != null) await _apply(rules, MatchMode, sBins, locations);
            Rules = rules;
            DraftSaved?.Invoke(preserveDraft ? CaptureDraft() : null);
            _error.Text = preserveDraft ? "已取消数据筛选，保留当前填写和勾选。"
                : "已应用，可以继续操作主窗口或修改条件。";
        }
        catch (OperationCanceledException) { _error.Text = "已取消计算，保留原筛选。"; }
        catch (Exception ex) { _error.Text = ex.Message; }
        finally { _applying = false; IsEnabled = true; RefreshFocusedRows(); }
    }
}
