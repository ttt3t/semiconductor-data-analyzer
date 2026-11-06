using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public partial class ManagementTrendView : UserControl
{
    private ManagementTrendData _data = new(Array.Empty<string>(), new());
    private List<WaferCategoryTrendWindow.CategoryChoice> _choices = new();
    public ManagementTrendView()
    {
        InitializeComponent();
    }
    public double ChartMinHeight { get => ChartRow.MinHeight; set => ChartRow.MinHeight = value; }
    public double TableMaxHeight
    {
        get => Table.MaxHeight;
        set { Table.MaxHeight = value; TableRow.MaxHeight = value; }
    }
    public void SetData(ManagementTrendData data)
    {
        var previous = _choices.ToDictionary(c => c.Category, c => c.IsVisible);
        foreach (var choice in _choices) choice.PropertyChanged -= Choice_Changed;
        _data = data;
        ReferencePanel.Visibility = data.Mode == ManagementChartMode.YieldBars ? Visibility.Visible : Visibility.Collapsed;
        Table.ItemsSource = null;
        while (Table.Columns.Count > 1) Table.Columns.RemoveAt(Table.Columns.Count - 1);
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        for (int i = 0; i < data.Labels.Length; i++) Table.Columns.Add(new DataGridTextColumn
        {
            Header = data.Labels[i].Replace(" / ", "\n"), Width = 82,
            Binding = new Binding($"Values[{i}]") { Mode = BindingMode.OneTime, StringFormat = data.Mode == ManagementChartMode.StackedCounts ? "{0:N0}" : "{0:F1}%", TargetNullValue = "–" }, ElementStyle = style
        });
        _choices = data.Series.Select(s => new WaferCategoryTrendWindow.CategoryChoice(s.Category, s.LineBrush)).ToList();
        bool restore = _choices.Any(c => previous.ContainsKey(c.Category));
        for (int i = 0; i < _choices.Count; i++)
            _choices[i].IsVisible = restore ? previous.GetValueOrDefault(_choices[i].Category, true) : data.Mode != ManagementChartMode.YieldBars || i == _choices.Count - 1;
        foreach (var choice in _choices) choice.PropertyChanged += Choice_Changed;
        Legend.ItemsSource = _choices;
        Chart.HorizontalOffset = 0; ResizeColumns(); Refresh();
    }
    public double TargetYield => double.TryParse(TxtTarget.Text, out double value) && double.IsFinite(value) && value is >= 0 and <= 100 ? value : 85;
    public ManagementTrendData SelectedData => _data with { Series = _data.Series.Where((_, i) => _choices[i].IsVisible).ToList(), TargetYield = TargetYield };
    private void Choice_Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        var selected = SelectedData; Table.ItemsSource = selected.Series; Chart.SetData(selected);
    }
    private void Target_Changed(object sender, RoutedEventArgs e) { if (Chart != null) Refresh(); }
    private void ResizeColumns()
    {
        if (_data.Labels.Length == 0) return;
        double width = Math.Max(74, (Table.ActualWidth - 152 - SystemParameters.VerticalScrollBarWidth) / _data.Labels.Length);
        foreach (var column in Table.Columns.Skip(1)) column.Width = width;
        Chart.ColumnWidth = width;
    }
    private void Table_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ResizeColumns(); }
    private void Table_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.HorizontalChange != 0) Chart.HorizontalOffset = e.HorizontalOffset; }
}
