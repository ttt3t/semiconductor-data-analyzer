using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class IniLayoutWindow : Window
{
    internal sealed record Target(int Index, CsvImportSource Source)
    {
        public string Name => ImportContainerService.DisplayName(Source);
    }

    private sealed class PreviewRow(int rowNumber, string[] cells) : INotifyPropertyChanged
    {
        public int Number { get; } = rowNumber;
        public string[] Cells { get; } = cells;
        public Brush? Marker { get; private set; }
        public string? FieldNames { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Mark(Brush? marker, string? fields)
        {
            Marker = marker;
            FieldNames = fields;
            PropertyChanged?.Invoke(this, new(nameof(Marker)));
            PropertyChanged?.Invoke(this, new(nameof(FieldNames)));
        }
    }

    private sealed record FieldInput(LayoutField Field, RadioButton Picker, TextBox Text);
    private sealed record DragSelection(FieldInput Input, int Start, int[] Keep);
    private sealed record Draft(string[] Positions, bool SkipRepeated);
    private readonly Func<string, int?, Task> _sync;
    private readonly Action? _cancelSync;
    private readonly string _fileName;
    private readonly List<FieldInput> _inputs = new();
    private readonly Dictionary<int, Draft> _drafts = new();
    private readonly List<TextBlock> _headers = new();
    private PreviewRow[] _rows = Array.Empty<PreviewRow>();
    private Target? _target;
    private CsvImportSource? _sample;
    private AnalyzerConfig _baseline = new();
    private string _text = "";
    private FieldInput? _active;
    private DragSelection? _drag;
    private CancellationTokenSource? _previewCancellation;
    private bool _closed, _saving, _updatingSheets, _updatingFields;

    internal IniLayoutWindow(string text, string scope, Func<string, int?, Task> sync,
        CsvImportSource? sample = null, IReadOnlyList<Target>? targets = null,
        string fileName = "config.ini", Action? cancelSync = null)
    {
        _sync = sync;
        _cancelSync = cancelSync;
        _fileName = fileName;
        InitializeComponent();
        ScopeText.Text = scope;
        foreach (var field in IniLayoutEditor.Fields) AddField(field);
        SelectField(_inputs[0]);
        SetDraft(text, null);
        _sample = sample;
        if (targets is { Count: > 0 })
        {
            TargetPanel.Visibility = Visibility.Visible;
            TargetList.ItemsSource = targets;
            TargetList.SelectedIndex = 0;
        }
        Loaded += async (_, _) =>
        {
            if (_sample != null) await LoadPreviewAsync(_sample);
        };
        Closing += (_, e) =>
        {
            if (!_saving) return;
            e.Cancel = true;
            _cancelSync?.Invoke();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _previewCancellation?.Cancel();
        };
    }

    private void AddField(LayoutField field)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 5) };
        var picker = new RadioButton { Content = field.Label, ToolTip = field.Tooltip, GroupName = "LayoutFields",
            Width = 128, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(picker, Dock.Left);
        row.Children.Add(picker);
        var text = new TextBox { ToolTip = field.Tooltip, Padding = new(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(text, field.Label);
        var input = new FieldInput(field, picker, text);
        _inputs.Add(input);
        picker.Checked += (_, _) => SelectField(input);
        text.GotKeyboardFocus += (_, _) => SelectField(input);
        text.TextChanged += (_, _) => { if (!_updatingFields) RefreshMarkers(); };
        if (field.Optional)
        {
            var clear = new Button { Content = field.IsMultiple ? "自动" : "无", MinWidth = 38, Margin = new(5, 0, 0, 0),
                ToolTip = field.IsMultiple ? "使用测项起始列" : "清除位置（0）" };
            DockPanel.SetDock(clear, Dock.Right);
            clear.Click += (_, _) =>
            {
                SelectField(input);
                text.Text = field.IsMultiple ? "" : "0";
            };
            row.Children.Add(clear);
        }
        row.Children.Add(text);
        (field.Section == "Layout" ? LayoutFields : IdentityFields).Children.Add(row);
    }

    private void SelectField(FieldInput input)
    {
        _active = input;
        input.Picker.IsChecked = true;
        string coordinate = input.Field.IsRow ? "行号" : "列标";
        string gesture = input.Field.IsMultiple ? $"拖选{coordinate}，按 Ctrl 补选" : $"点击{coordinate}";
        SelectionHelp.Text = $"{input.Field.Label} · {gesture}，也可输入序号";
        RefreshMarkers();
    }

    private void SetDraft(string text, Draft? draft)
    {
        _text = text;
        _baseline = ConfigLoader.Parse(text, _fileName);
        _updatingFields = true;
        for (int i = 0; i < _inputs.Count; i++)
            _inputs[i].Text.Text = draft?.Positions[i] ?? _inputs[i].Field.Value(_baseline);
        SkipRepeated.IsChecked = draft?.SkipRepeated ?? _baseline.SkipRepeatedHeaders;
        _updatingFields = false;
        RefreshMarkers();
    }

    private async void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetList.SelectedItem is not Target target || target == _target) return;
        if (_target is { } previous)
            _drafts[previous.Index] = new(_inputs.Select(input => input.Text.Text).ToArray(), SkipRepeated.IsChecked == true);
        _target = target;
        SetDraft(target.Source.ConfigText, _drafts.GetValueOrDefault(target.Index));
        if (IsLoaded) await LoadPreviewAsync(target.Source);
        else _sample = target.Source;
    }

    private async void OpenSample_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "打开布局样本",
            Filter = "CSV / Excel 表格|*.csv;*.txt;*.tsv;*.xlsx;*.xlsm|CSV / 文本|*.csv;*.txt;*.tsv|Excel 工作簿|*.xlsx;*.xlsm" };
        if (dialog.ShowDialog(this) == true) await LoadPreviewAsync(new(dialog.FileName));
    }

    private async void Worksheet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSheets && _sample != null && SheetList.SelectedItem is ImportWorksheet sheet)
            await LoadPreviewAsync(_sample with { Worksheet = sheet.Name });
    }

    private async Task LoadPreviewAsync(CsvImportSource source)
    {
        _previewCancellation?.Cancel();
        bool sameFile = _sample is { } previous && previous.Path == source.Path && previous.ArchiveEntry == source.ArchiveEntry;
        _sample = source;
        if (!sameFile) SetSheets(Array.Empty<ImportWorksheet>(), null);
        DisplayRows(Array.Empty<string[]>());
        SampleName.Text = ImportContainerService.DisplayName(source);
        SampleName.ToolTip = source.Path;
        SampleStatus.Text = "正在读取前 50 行 × 50 列…";
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        SyncButton.IsEnabled = false;
        var baseline = _baseline;
        try
        {
            var preview = await Task.Run(() => IniLayoutPreview.Read(source, baseline, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            SetSheets(preview.Sheets, preview.Worksheet);
            _sample = source with { Worksheet = preview.Worksheet };
            SampleName.Text = ImportContainerService.DisplayName(_sample);
            DisplayRows(preview.Rows);
            int width = _headers.Count;
            SampleStatus.Text = width == 0 ? "预览范围内没有单元格数据，可以切换工作表或输入坐标。"
                : $"显示 {preview.Rows.Length} 行 × {width} 列（最多 50 × 50）。";
            if (preview.UncachedFormulas > 0)
                SampleStatus.Text += $" {preview.UncachedFormulas} 个公式没有保存结果，显示为空。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_previewCancellation, cancellation))
                SampleStatus.Text = "预览失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                if (!_closed) SyncButton.IsEnabled = true;
            }
        }
    }

    private void SetSheets(ImportWorksheet[] sheets, string? selected)
    {
        _updatingSheets = true;
        SheetList.ItemsSource = sheets;
        SheetList.SelectedItem = sheets.FirstOrDefault(sheet => sheet.Name == selected);
        SheetList.Visibility = sheets.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingSheets = false;
    }

    private void DisplayRows(string[][] values)
    {
        _drag = null;
        if (Mouse.Captured == SampleGrid) Mouse.Capture(null);
        SampleGrid.ItemsSource = null;
        SampleGrid.Columns.Clear();
        _headers.Clear();
        int width = values.Length == 0 ? 0 : values.Max(row => row.Length);
        var rowStyle = new Style(typeof(DataGridCell));
        rowStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, new Binding(nameof(PreviewRow.Marker))));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(PreviewRow.FieldNames))));
        SampleGrid.Columns.Add(new DataGridTextColumn { Header = "行", Binding = new Binding(nameof(PreviewRow.Number)),
            Width = 45, CanUserResize = false, CellStyle = rowStyle });
        for (int column = 1; column <= width; column++)
        {
            string letters = column <= 26 ? ((char)('A' + column - 1)).ToString() :
                string.Concat('A', (char)('A' + column - 27));
            var header = new TextBlock { Text = letters, Padding = new(7, 2, 7, 2) };
            _headers.Add(header);
            SampleGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding($"Cells[{column - 1}]"),
                Width = 105, MinWidth = 46 });
        }
        _rows = values.Select((row, index) =>
        {
            Array.Resize(ref row, width);
            return new PreviewRow(index + 1, row);
        }).ToArray();
        SampleGrid.ItemsSource = _rows;
        RefreshMarkers();
    }

    private void RefreshMarkers()
    {
        var mapped = _inputs.SelectMany(input => VisiblePositions(input).Select(position => (Input: input, Position: position)))
            .ToLookup(binding => (binding.Input.Field.IsRow, binding.Position));
        var active = (Brush)FindResource("AccentBrush");
        var inactive = (Brush)FindResource("AccentSoft");
        for (int index = 0; index < _headers.Count; index++)
        {
            var fields = mapped[(false, index + 1)].Select(binding => binding.Input).ToArray();
            bool selected = fields.Contains(_active);
            var header = _headers[index];
            header.Background = selected ? active : fields.Length > 0 ? inactive : Brushes.Transparent;
            header.Foreground = selected ? Brushes.White : (Brush)FindResource("TextPrimary");
            header.ToolTip = (index + 1).ToString(CultureInfo.InvariantCulture) +
                (fields.Length == 0 ? "" : " · " + string.Join("、", fields.Select(field => field.Field.Label)));
        }
        foreach (var row in _rows)
        {
            var fields = mapped[(true, row.Number)].Select(binding => binding.Input).ToArray();
            row.Mark(fields.Contains(_active) ? (Brush)FindResource("AccentSoft") :
                fields.Length > 0 ? Brushes.AliceBlue : Brushes.Transparent,
                fields.Length == 0 ? null : string.Join("、", fields.Select(field => field.Field.Label)));
        }
        var selectedColumns = mapped.SelectMany(group => group)
            .Where(binding => binding.Input.Field.Key == "TestItemColumns").Select(binding => binding.Position);
        var identity = mapped.SelectMany(group => group)
            .Where(binding => binding.Input.Field.Section == "IdentityColumns").Select(binding => binding.Position);
        var overlaps = selectedColumns.Intersect(identity).Order().ToArray();
        SaveStatus.Text = overlaps.Length == 0 ? "" : $"第 {string.Join("、", overlaps)} 列也绑定了身份字段，解析时会跳过这些测项列。";
    }

    private static int[] VisiblePositions(FieldInput field)
    {
        try { return field.Field.ReadPositions(field.Text.Text).Where(position => position > 0).ToArray(); }
        catch (InvalidDataException) { return Array.Empty<int>(); }
    }

    private void Grid_DragStart(object sender, MouseButtonEventArgs e)
    {
        if (_active is not { } field || e.OriginalSource is not DependencyObject source ||
            FindAncestor<Thumb>(source) != null || GridPosition(source, field.Field) is not int start) return;
        int[] keep = field.Field.IsMultiple && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? VisiblePositions(field) : Array.Empty<int>();
        _drag = new(field, start, keep);
        SetDragPosition(start);
        Mouse.Capture(SampleGrid, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void Grid_DragMove(object sender, MouseEventArgs e)
    {
        if (_drag is { Input.Field.IsMultiple: true } drag && e.LeftButton == MouseButtonState.Pressed &&
            SampleGrid.InputHitTest(e.GetPosition(SampleGrid)) is DependencyObject source &&
            GridPosition(source, drag.Input.Field) is int position)
            SetDragPosition(position);
    }

    private void SetDragPosition(int end)
    {
        var drag = _drag!;
        var positions = Enumerable.Range(Math.Min(drag.Start, end), Math.Abs(drag.Start - end) + 1);
        string text = string.Join(",", drag.Keep.Concat(positions).Distinct().Order());
        if (drag.Input.Text.Text != text) drag.Input.Text.Text = text;
    }

    private void Grid_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null) return;
        _drag = null;
        if (Mouse.Captured == SampleGrid) Mouse.Capture(null);
        e.Handled = true;
    }

    private void Grid_LostCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == SampleGrid) _drag = null;
    }

    private static int? GridPosition(DependencyObject source, LayoutField field)
    {
        if (field.IsRow) return FindAncestor<DataGridRow>(source)?.Item is PreviewRow row ? row.Number : null;
        int? index = FindAncestor<DataGridColumnHeader>(source)?.Column?.DisplayIndex ??
            FindAncestor<DataGridCell>(source)?.Column?.DisplayIndex;
        return index > 0 ? index : null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T item) return item;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : (node as FrameworkContentElement)?.Parent;
        }
        return null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _previewCancellation != null) return;
        try
        {
            var coordinates = _inputs.ToDictionary(input => input.Field, input => input.Field.ReadPositions(input.Text.Text));
            string text = IniLayoutEditor.Apply(_text, _baseline, coordinates, SkipRepeated.IsChecked == true);
            _saving = true;
            SampleControls.IsEnabled = EditorBody.IsEnabled = SyncButton.IsEnabled = false;
            CancelButton.IsEnabled = _cancelSync != null;
            CancelButton.Content = "取消处理";
            SaveStatus.Text = "正在同步配置…";
            await _sync(text, _target?.Index);
            _saving = false;
            IndependentWindow.Accept(this);
        }
        catch (OperationCanceledException) { SaveStatus.Text = "已取消同步，可以继续修改。"; }
        catch (Exception ex) { SaveStatus.Text = "同步失败：" + ex.Message; }
        finally
        {
            _saving = false;
            SampleControls.IsEnabled = EditorBody.IsEnabled = SyncButton.IsEnabled = CancelButton.IsEnabled = true;
            CancelButton.Content = "取消";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) _cancelSync?.Invoke();
        else Close();
    }
}
