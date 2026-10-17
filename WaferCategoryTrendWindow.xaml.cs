using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class WaferCategoryTrendWindow : Window
{
    public sealed class CategoryChoice : INotifyPropertyChanged
    {
        public string Category { get; }
        public Brush LineBrush { get; }
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible == value) return; _isVisible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public CategoryChoice(string category, Brush lineBrush) { Category = category; LineBrush = lineBrush; }
    }

    private readonly string[] _wafers = Array.Empty<string>();
    private readonly List<CategoryChoice> _choices;
    private WaferStatisticsResult? _result;
    private bool _firstMode, _updating;

    public WaferCategoryTrendWindow(IEnumerable<string> wafers, IEnumerable<string> categories)
    {
        InitializeComponent();
        _wafers = wafers.Distinct(StringComparer.Ordinal).ToArray();
        var names = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var colorKeys = Enumerable.Range(2, names.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var colors = BinPalette.Create(colorKeys);
        _choices = names.Select((n, i) => new CategoryChoice(n, colors[colorKeys[i]])).ToList();
        foreach (var choice in _choices) choice.PropertyChanged += Choice_Changed;
        CategoryChoices.ItemsSource = _choices;
        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        cellStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        for (int i = 0; i < _wafers.Length; i++)
            DgTrend.Columns.Add(new DataGridTextColumn
            {
                Header = _wafers[i], Width = WaferCategoryTrendControl.WaferColumnWidth,
                MinWidth = WaferCategoryTrendControl.WaferColumnWidth,
                Binding = new Binding($"Values[{i}]") { Mode = BindingMode.OneTime, StringFormat = "{0:F2}%", TargetNullValue = "–" },
                ElementStyle = cellStyle
            });
        var layout = new WindowLayoutPersistence(this, "WaferCategoryTrend");
        layout.TrackRows("ChartAndTable", ChartRow, TableRow);
        Closed += (_, _) =>
        {
            foreach (var choice in _choices) choice.PropertyChanged -= Choice_Changed;
            _choices.Clear(); _result = null;
            CategoryChoices.ItemsSource = DgTrend.ItemsSource = null;
            TrendChart.SetData(Array.Empty<string>(), Array.Empty<WaferCategorySeries>());
        };
    }

    public void UpdateStatistics(WaferStatisticsResult result, bool firstMode)
    {
        if (ReferenceEquals(_result, result) && _firstMode == firstMode) return;
        _result = result; _firstMode = firstMode;
        TxtMode.Text = $"{(firstMode ? "只保留首测" : "复测覆盖首测")}  ·  {_wafers.Length} 个晶圆  ·  SBIN 分类占比（%）";
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var indices = _wafers.Select(w => _result?.Wafers.IndexOf(w) ?? -1).ToArray();
        var source = _result?.CategoryRows.ToDictionary(r => r.Category, StringComparer.OrdinalIgnoreCase);
        var rows = _choices.Where(c => c.IsVisible).Select(choice =>
        {
            var row = source?.GetValueOrDefault(choice.Category);
            return new WaferCategorySeries(choice.Category, choice.LineBrush, indices.Select(i =>
                i < 0 || _result == null || i >= _result.ValidSBinCounts.Length || _result.ValidSBinCounts[i] == 0
                    ? (double?)null : row == null ? 0 : row.Percentages[i]).ToArray());
        }).ToList();
        DgTrend.ItemsSource = rows;
        TrendChart.SetData(_wafers, rows);
        TxtSelection.Text = $"已显示 {rows.Count} / {_choices.Count} 个分类";
    }

    private void Choice_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating) RefreshSelection();
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SelectAll(false);
    private void SelectAll(bool visible)
    {
        _updating = true;
        try { foreach (var choice in _choices) choice.IsVisible = visible; }
        finally { _updating = false; }
        RefreshSelection();
    }
    private void Table_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0) TrendChart.HorizontalOffset = e.HorizontalOffset;
    }
    private void Table_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _wafers.Length == 0) return;
        // Fill a short wafer list, but keep pixel widths so a long list stays column-virtualized.
        double available = Math.Max(0, DgTrend.ActualWidth - WaferCategoryTrendControl.LabelColumnWidth - SystemParameters.VerticalScrollBarWidth - 2);
        double width = Math.Max(WaferCategoryTrendControl.WaferColumnWidth, available / _wafers.Length);
        foreach (var column in DgTrend.Columns.Skip(1)) column.Width = width;
        TrendChart.ColumnWidth = width;
    }
}
