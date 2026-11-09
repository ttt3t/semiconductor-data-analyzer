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

    private void SetStackPages(IReadOnlyList<WaferMapPage> pages)
    {
        _stackPages = pages;
        // Virtualize rows, each containing at most four existing wafer snapshots.
        // No per-die UI objects or extra heatmap-colored die lists are allocated.
        StackRows.ItemsSource = new ListCollectionView(pages.Chunk(4).ToArray());
    }
    private void StackMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null || StackRows == null) return;
        bool stacked = ChkStacked.IsChecked == true;
        Map.ClearHover();
        SingleMapView.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        StackRows.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        StackHint.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        WaferSidebar.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        WaferSidebarColumn.Width = new GridLength(stacked ? 0 : 140);
        if (stacked && WaferList.SelectedItem is WaferMapPage selected)
            for (int i = 0; i < _stackPages.Count; i++)
                if (_stackPages[i].WaferId == selected.WaferId) { StackRows.ScrollIntoView(StackRows.Items[i / 4]); break; }
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
