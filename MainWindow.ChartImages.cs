using System.Runtime.InteropServices;
using System.Windows;
using SemiconductorCsvAnalyzer.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private ChartImageSize _histogramImageSize = new(1440, 400);
    private ChartImageSize _scatterImageSize = new(1440, 400);

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement chart || !chart.IsVisible || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        if (ReferenceEquals(chart, Histogram)) _histogramImageSize = ChartImage.SizeOf(chart);
        else _scatterImageSize = ChartImage.SizeOf(chart);
    }

    private ChartImageSize ReportChartSize(FrameworkElement chart, ChartImageSize lastSize)
    {
        if (chart.IsVisible && chart.ActualWidth > 0 && chart.ActualHeight > 0) return ChartImage.SizeOf(chart);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(chart);
        return lastSize with { DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY };
    }

    private void CopyHistogram_Click(object sender, RoutedEventArgs e) => CopyChart(Histogram, "直方图");
    private void CopyScatter_Click(object sender, RoutedEventArgs e) => CopyChart(ScatterPlot, "散点图");

    private void CopyChart(FrameworkElement chart, string name)
    {
        try
        {
            ChartImage.Copy(chart);
            var size = ChartImage.SizeOf(chart);
            TxtStatus.Text = $"已复制{name} PNG（{size.PixelWidth} × {size.PixelHeight}）";
        }
        catch (ExternalException) { TxtStatus.Text = "剪贴板正忙，请重试"; }
        catch (Exception ex) { TxtStatus.Text = $"复制{name}失败：{ex.Message}"; }
    }
}
