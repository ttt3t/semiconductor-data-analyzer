using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class WaferMapWindow
{
    private IReadOnlyList<WaferMapPage> _stackPages = Array.Empty<WaferMapPage>();
    private readonly Dictionary<string, (int Count, Brush Color)> _stackBinCounts = new(StringComparer.Ordinal);
    private int _stackBinTotal;

    private void SetStackPages(IReadOnlyList<WaferMapPage> pages)
    {
        _stackPages = pages;
        ResetCumulativeData();
        // Cache only per-BIN counts/colors, so selecting a wafer does not rescan every die.
        _stackBinCounts.Clear(); _stackBinTotal = 0;
        if (_isBinMap)
            foreach (var page in pages)
                foreach (var die in page.Dies)
                {
                    _stackBinCounts[die.Category] = _stackBinCounts.TryGetValue(die.Category, out var entry)
                        ? (entry.Count + 1, entry.Color) : (1, die.Color);
                    _stackBinTotal++;
                }
        // Virtualize rows, each containing at most four existing wafer snapshots.
        // No per-die UI objects or extra heatmap-colored die lists are allocated.
        StackRows.ItemsSource = new ListCollectionView(pages.Chunk(4).ToArray());
    }
    private void StackMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null || StackRows == null || _changingMapView) return;
        if (ChkStacked.IsChecked == true && IsCumulative)
        {
            _changingMapView = true;
            try { ChkCumulative.IsChecked = false; }
            finally { _changingMapView = false; }
            CancelCumulativeBuild();
            CumulativeRows.ItemsSource = null;
        }
        ApplyMapView();
    }
    private void ApplyMapView()
    {
        bool stacked = ChkStacked.IsChecked == true;
        bool multiple = stacked || IsCumulative;
        Map.ClearHover();
        SingleMapView.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;
        StackRows.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        CumulativeRows.Visibility = IsCumulative ? Visibility.Visible : Visibility.Collapsed;
        StackHint.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        StackHint.Text = IsCumulative ? "0% 浅灰，100% 原色；悬停查看次数，单击选择复制" :
            _isBinMap ? "图例汇总全部晶圆，双击进入单片" : "单击晶圆查看图例，双击进入单片";
        WaferSidebar.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;
        WaferSidebarColumn.Width = new GridLength(multiple ? 0 : 140);
        ChkBinNumbers.IsEnabled = ChkSiteBorders.IsEnabled = !IsCumulative;
        BtnCopyMapTable.Content = IsCumulative ? "复制累计占比表格" : "复制 Map 表格";
        BtnCopyMapTable.ToolTip = IsCumulative ? "复制当前选中 Bin / P / F 的逐坐标占比（百分比），带数字坐标轴" :
            "复制当前晶圆的坐标表格，可粘贴到 Excel；SBIN/HBIN 填编号，P/F 填 P 或 F，无效测量填 E，空坐标留空";
        if (stacked && WaferList.SelectedItem is WaferMapPage selected)
            for (int i = 0; i < _stackPages.Count; i++)
                if (_stackPages[i].WaferId == selected.WaferId) { StackRows.ScrollIntoView(StackRows.Items[i / 4]); break; }
        RefreshLegend();
        MapMode_Changed(this, new RoutedEventArgs());
    }
    private void DisplayOptions_Changed(object sender, RoutedEventArgs e) => RefreshVisibleStackTiles();
    private void StackTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is WaferMapTile tile) ApplyTileOptions(tile);
    }
    private void StackTile_Selected(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WaferMapTile { Page: { } page }) return;
        SelectWafer(page.WaferId);
        if (e.ClickCount == 2) ChkStacked.IsChecked = false;
        e.Handled = true;
    }
    private void RefreshVisibleStackTiles()
    {
        if (!_ready || StackRows == null || ChkStacked.IsChecked != true) return;
        foreach (var tile in VisualChildren<WaferMapTile>(StackRows)) ApplyTileOptions(tile);
    }
    private void ApplyTileOptions(WaferMapTile tile)
    {
        tile.SetOptions(new(HeatMode.IsChecked == true && _heatmapScale != null, _heatmapScale,
            Map.DimmedCategories, _isBinMap && ChkBinNumbers.IsChecked == true, ChkSiteBorders.IsChecked == true),
            tile.Page?.WaferId == (WaferList.SelectedItem as WaferMapPage)?.WaferId);
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
}
