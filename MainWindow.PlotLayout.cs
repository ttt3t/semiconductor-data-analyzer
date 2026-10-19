using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private string _plotView = "Both";
    private double _bothChartRatio = 1.1 / 2.1;
    private bool _updatingPlotView;

    private void PlotView_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingPlotView || sender is not RadioButton { Tag: string mode }) return;
        SetPlotView(mode);
    }

    private void SetPlotView(string mode)
    {
        if (HistogramPanel == null || ScatterPanel == null) return;
        if (_plotView == "Both" && mode != "Both")
        {
            double total = HistogramRow.ActualHeight + ScatterRow.ActualHeight;
            if (total > 0 && HistogramRow.ActualHeight > 30 && ScatterRow.ActualHeight > 30)
                _bothChartRatio = HistogramRow.ActualHeight / total;
        }
        _plotView = mode;
        bool both = mode == "Both", histogram = mode != "Scatter", scatter = mode != "Histogram";
        HistogramPanel.Visibility = histogram ? Visibility.Visible : Visibility.Collapsed;
        ScatterPanel.Visibility = scatter ? Visibility.Visible : Visibility.Collapsed;
        PlotDivider.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        PlotDividerRow.Height = new GridLength(both ? 8 : 0);
        HistogramRow.Height = histogram ? new GridLength(both ? _bothChartRatio : 1, GridUnitType.Star) : new GridLength(0);
        ScatterRow.Height = scatter ? new GridLength(both ? 1 - _bothChartRatio : 1, GridUnitType.Star) : new GridLength(0);
        _updatingPlotView = true;
        try { PlotBoth.IsChecked = both; PlotHistogram.IsChecked = mode == "Histogram"; PlotScatter.IsChecked = mode == "Scatter"; }
        finally { _updatingPlotView = false; }
    }

    private void PlotDivider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (HistogramRow.ActualHeight <= 30) SetPlotView("Scatter");
        else if (ScatterRow.ActualHeight <= 30) SetPlotView("Histogram");
        else _bothChartRatio = HistogramRow.ActualHeight / (HistogramRow.ActualHeight + ScatterRow.ActualHeight);
    }
    private void PlotDivider_DoubleClick(object sender, MouseButtonEventArgs e)
    { SetPlotView("Both"); e.Handled = true; }
}
