using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public partial class ManagementTrendView : UserControl
{
    private ManagementTrendData _data = new(Array.Empty<string>(), new());
    private List<WaferCategoryTrendWindow.CategoryChoice> _choices = new();
    private double _widthZoom = 1, _columnWidth, _headerWidth, _rightPadding;
    private double? _pendingOffset;
    private ManagementChartLayout? _lastLayout;
    public ManagementTrendView()
    {
        InitializeComponent();
        Loaded += (_, _) => ResizeColumns();
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
        CountDisplayPanel.Visibility = data.IsStacked ? Visibility.Visible : Visibility.Collapsed;
        Table.ItemsSource = null;
        while (Table.Columns.Count > 1) Table.Columns.RemoveAt(Table.Columns.Count - 1);
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        for (int i = 0; i < data.Labels.Length; i++) Table.Columns.Add(new DataGridTextColumn
        {
            Header = data.Labels[i].Replace(" / ", "\n"), Width = 82, MinWidth = 0,
            Binding = new Binding($"Values[{i}]") { Mode = BindingMode.OneTime, StringFormat = ValueFormat, TargetNullValue = "–" }, ElementStyle = style
        });
        _choices = data.Series.Select(s => new WaferCategoryTrendWindow.CategoryChoice(s.Category, s.LineBrush)).ToList();
        bool restore = _choices.Any(c => previous.ContainsKey(c.Category));
        for (int i = 0; i < _choices.Count; i++)
            _choices[i].IsVisible = restore ? previous.GetValueOrDefault(_choices[i].Category, true) : data.Mode != ManagementChartMode.YieldBars || i == _choices.Count - 1;
        foreach (var choice in _choices) choice.PropertyChanged += Choice_Changed;
        Legend.ItemsSource = _choices;
        _pendingOffset = 0; Chart.HorizontalOffset = 0; ResizeColumns(); Refresh();
    }
    public double TargetYield => double.TryParse(TxtTarget.Text, out double value) && double.IsFinite(value) && value is >= 0 and <= 100 ? value : 85;
    public ManagementCountDisplay CountDisplay
    {
        get => FailPercentMode?.IsChecked == true ? ManagementCountDisplay.PercentOfFailures : TotalPercentMode?.IsChecked == true ? ManagementCountDisplay.PercentOfTotal : ManagementCountDisplay.Count;
        set
        {
            if (value == ManagementCountDisplay.PercentOfFailures) FailPercentMode.IsChecked = true;
            else if (value == ManagementCountDisplay.PercentOfTotal) TotalPercentMode.IsChecked = true;
            else CountMode.IsChecked = true;
        }
    }
    private string ValueFormat => _data.Mode == ManagementChartMode.StackedCounts && CountDisplay == ManagementCountDisplay.Count ? "{0:N0}" : "{0:F1}%";
    public ManagementTrendData SelectedData
    {
        get
        {
            var data = ManagementTrendService.DisplayCounts(_data, CountDisplay);
            return data with { Series = data.Series.Where((_, i) => _choices[i].IsVisible).ToList(), TargetYield = TargetYield };
        }
    }
    private void Choice_Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        var selected = SelectedData; Table.ItemsSource = selected.Series; Chart.SetData(selected);
    }
    private void Target_Changed(object sender, RoutedEventArgs e) { if (Chart != null) Refresh(); }
    private void DisplayMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Table == null || Chart == null) return;
        foreach (var column in Table.Columns.OfType<DataGridTextColumn>())
            if (column.Binding is Binding binding) column.Binding = new Binding(binding.Path.Path)
            { Mode = BindingMode.OneTime, StringFormat = ValueFormat, TargetNullValue = "–" };
        Refresh();
    }
    public double WidthZoom => _widthZoom;
    public ManagementChartLayout? ReportLayout
    {
        get { CaptureLayout(); return _lastLayout; }
    }
    public void ZoomWidth(double factor, double anchor = 0)
    {
        if (!_data.IsStacked || !double.IsFinite(factor) || factor <= 0) return;
        double zoom = Math.Clamp(_widthZoom * factor, .1, 8);
        var scroll = FindScrollViewer(Table);
        double offset = _pendingOffset ?? scroll?.HorizontalOffset ?? 0;
        _pendingOffset = Math.Max(0, (offset + Math.Max(0, anchor)) * zoom / _widthZoom - Math.Max(0, anchor));
        _widthZoom = zoom;
        TxtWidthZoom.Text = zoom.ToString("P0");
        ResizeColumns();
    }
    private void WidthZoom_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_data.IsStacked || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0) return;
        ZoomWidth(Math.Pow(1.15, e.Delta / 120.0), Math.Max(0, e.GetPosition(Chart).X - _headerWidth - 1));
        e.Handled = true;
    }
    private void WidthZoom_Reset(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        _pendingOffset = 0; ZoomWidth(1 / _widthZoom); e.Handled = true;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private void CaptureLayout()
    {
        if (_data.IsStacked && _columnWidth > 0 && Chart.ActualHeight >= 100)
            _lastLayout = new(_columnWidth, _headerWidth, Chart.ActualHeight, Table.RowHeight, Table.ColumnHeaderHeight, _rightPadding);
    }
    private void ResizeColumns()
    {
        // An inactive tab has no viewport yet. Zero-width columns prevent WPF's
        // virtualized grid from estimating its cells; size them when it is loaded.
        if (_data.Labels.Length == 0 || Table.ActualWidth <= 0) return;
        double header = Math.Min(150, Table.ActualWidth * .25);
        double available = Table.ActualWidth - header - 2 - SystemParameters.VerticalScrollBarWidth;
        if (available <= 0) return;
        double width = available / _data.Labels.Length * (_data.IsStacked ? _widthZoom : 1);
        Table.Columns[0].Width = header;
        foreach (var column in Table.Columns.Skip(1)) column.Width = width;
        _columnWidth = width; _headerWidth = header; _rightPadding = 1 + SystemParameters.VerticalScrollBarWidth;
        if (_data.IsStacked) Chart.SetColumnLayout(width, header + 1, _rightPadding);
        else Chart.SetColumnLayout();
        CaptureLayout();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_pendingOffset is not double offset || !IsLoaded) return;
            _pendingOffset = null;
            FindScrollViewer(Table)?.ScrollToHorizontalOffset(offset);
        }));
    }
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => CaptureLayout();
    private void Table_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ResizeColumns(); }
    private void Table_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0) Chart.HorizontalOffset = e.HorizontalOffset;
    }
}
