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
        InitializeComponent(); Chart.FitYAxisToData = true; Chart.AxisTitle = "良率（%）";
    }
    public void SetData(ManagementTrendData data)
    {
        foreach (var choice in _choices) choice.PropertyChanged -= Choice_Changed;
        _data = data;
        Table.ItemsSource = null;
        while (Table.Columns.Count > 1) Table.Columns.RemoveAt(Table.Columns.Count - 1);
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        for (int i = 0; i < data.Labels.Length; i++) Table.Columns.Add(new DataGridTextColumn
        {
            Header = data.Labels[i], Width = 120,
            Binding = new Binding($"Values[{i}]") { Mode = BindingMode.OneTime, StringFormat = "{0:F2}%", TargetNullValue = "–" }, ElementStyle = style
        });
        _choices = data.Series.Select(s => new WaferCategoryTrendWindow.CategoryChoice(s.Category, s.LineBrush)).ToList();
        foreach (var choice in _choices) choice.PropertyChanged += Choice_Changed;
        Legend.ItemsSource = _choices;
        Chart.HorizontalOffset = 0; ResizeColumns(); Refresh();
    }
    public ManagementTrendData SelectedData => new(_data.Labels,
        _data.Series.Where((_, i) => _choices[i].IsVisible).ToList());
    private void Choice_Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        var selected = SelectedData; Table.ItemsSource = selected.Series; Chart.SetData(selected.Labels, selected.Series);
    }
    private void ResizeColumns()
    {
        if (_data.Labels.Length == 0) return;
        double width = Math.Max(120, (Table.ActualWidth - 202 - SystemParameters.VerticalScrollBarWidth) / _data.Labels.Length);
        foreach (var column in Table.Columns.Skip(1)) column.Width = width;
        Chart.ColumnWidth = width;
    }
    private void Table_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ResizeColumns(); }
    private void Table_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.HorizontalChange != 0) Chart.HorizontalOffset = e.HorizontalOffset; }
}
