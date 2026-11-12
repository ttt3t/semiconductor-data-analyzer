using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public static class ImportSourceSelection
{
    private sealed record Candidate(CsvImportSource Source, bool Selected);

    public static async Task<CsvImportSource[]?> SelectAsync(IReadOnlyList<string> paths)
    {
        var sources = paths.Select(p => new CsvImportSource(Path.GetFullPath(p))).ToArray();
        if (sources.Any(s => ImportContainerService.IsZip(s.Path)))
        {
            var picker = new Picker("选择 ZIP 内部文件", "勾选要导入的文件。可按文件名、目录或扩展名搜索，再全选当前显示项。", token =>
            {
                var entries = new List<Candidate>();
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ImportContainerService.IsZip(source.Path)) { entries.Add(new(source, true)); continue; }
                    var files = ImportContainerService.ZipEntries(source.Path, token);
                    entries.AddRange(files.Select(f => new Candidate(f, files.Length == 1)));
                }
                return entries.ToArray();
            });
            if (!await IndependentWindow.ShowAsync(picker)) return null;
            sources = picker.Selected;
        }
        if (sources.Any(s => ImportContainerService.IsSpreadsheet(s.ArchiveEntry ?? s.Path)))
        {
            var selectedSources = sources;
            var picker = new Picker("选择 Excel 工作表", "选择含测试数据的工作表。默认勾选每个工作簿的第一个可见工作表，可多选；行列定义沿用当前 CSV / INI 配置。", token =>
            {
                var sheets = new List<Candidate>();
                foreach (var source in selectedSources)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ImportContainerService.IsSpreadsheet(source.ArchiveEntry ?? source.Path)) { sheets.Add(new(source, true)); continue; }
                    var definitions = SpreadsheetImportService.Worksheets(source, token);
                    var first = definitions.FirstOrDefault(s => !s.Hidden) ?? definitions[0];
                    sheets.AddRange(definitions.Select(s => new Candidate(source with { Worksheet = s.Name }, s == first)));
                }
                return sheets.ToArray();
            });
            if (!await IndependentWindow.ShowAsync(picker)) return null;
            sources = picker.Selected;
        }
        return sources;
    }

    private sealed class Picker : Window
    {
        public sealed class Row : INotifyPropertyChanged
        {
            public CsvImportSource Source { get; }
            public string Name => ImportContainerService.DisplayName(Source);
            public string Location => Source.Path + (Source.ArchiveEntry == null ? "" : " ▸ " + Source.ArchiveEntry);
            private bool _selected;
            public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
            public event PropertyChangedEventHandler? PropertyChanged;
            public Row(Candidate candidate) { Source = candidate.Source; _selected = candidate.Selected; }
        }
        private readonly TextBox _search = new();
        private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 25, SelectionMode = DataGridSelectionMode.Extended };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        private readonly Button _open = new() { Content = "继续", Padding = new Thickness(18, 5, 18, 5), IsEnabled = false, IsDefault = true };
        private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 8, 0, 8) };
        private readonly CancellationTokenSource _cancel = new();
        private Row[] _rows = Array.Empty<Row>();
        private ListCollectionView? _view;
        private bool _closed, _loading;
        public CsvImportSource[] Selected { get; private set; } = Array.Empty<CsvImportSource>();

        public Picker(string title, string instruction, Func<CancellationToken, Candidate[]> load)
        {
            Title = title; Width = 900; Height = 560; FontFamily = new("Microsoft YaHei UI"); FontSize = 12;
            var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
            var note = new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0) };
            actions.Children.Add(cancel); actions.Children.Add(_open);
            DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
            DockPanel.SetDock(_progress, Dock.Bottom); root.Children.Add(_progress);
            Button SelectButton(string text, bool selected)
            {
                var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8, 0, 0, 0) };
                button.Click += (_, _) => { foreach (var row in _grid.Items.OfType<Row>()) row.Selected = selected; UpdateCount(); };
                DockPanel.SetDock(button, Dock.Right); return button;
            }
            tools.Children.Add(SelectButton("取消显示项", false)); tools.Children.Add(SelectButton("全选显示项", true));
            tools.Children.Add(_search); _search.TextChanged += (_, _) => _view?.Refresh();
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "选择", Binding = new Binding(nameof(Row.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 55 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "文件 / ZIP 内部路径 / 工作表", Binding = new Binding(nameof(Row.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Row.Location)))); _grid.RowStyle = rowStyle;
            root.Children.Add(_grid);
            _open.Click += (_, _) =>
            {
                _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
                Selected = _rows.Where(r => r.Selected).Select(r => r.Source).ToArray();
                if (Selected.Length == 0) { _status.Text = "请至少勾选一项。"; return; }
                IndependentWindow.Accept(this);
            };
            Loaded += async (_, _) =>
            {
                _loading = true; _status.Text = "正在读取文件列表…";
                try
                {
                    var candidates = await Task.Run(() => load(_cancel.Token), _cancel.Token);
                    if (_closed) return;
                    _rows = candidates.Select(c => new Row(c)).ToArray();
                    foreach (var row in _rows) row.PropertyChanged += RowChanged;
                    _view = new ListCollectionView(_rows) { Filter = value => value is Row row && row.Name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) };
                    _grid.ItemsSource = _view; _open.IsEnabled = true; UpdateCount();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!_closed) _status.Text = "无法读取：" + ex.Message; }
                finally { _loading = false; if (!_closed) _progress.Visibility = Visibility.Collapsed; else _cancel.Dispose(); }
            };
            Closed += (_, _) =>
            {
                _closed = true; _cancel.Cancel(); if (!_loading) _cancel.Dispose();
                foreach (var row in _rows) row.PropertyChanged -= RowChanged;
                _grid.ItemsSource = null; _view = null;
            };
        }
        private void RowChanged(object? sender, PropertyChangedEventArgs e) => UpdateCount();
        private void UpdateCount() => _status.Text = $"共 {_rows.Length} 项，已勾选 {_rows.Count(r => r.Selected)} 项（包括被搜索隐藏的已勾选项）。";
    }
}
