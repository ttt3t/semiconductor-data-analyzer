using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class WaferMapWindow
{
    private bool _changingMapView;
    private long _cumulativeVersion;
    private CancellationTokenSource? _cumulativeCancellation;
    private CumulativeMapData? _cumulativeData;
    private CumulativeMapCategory? _selectedCumulative;
    private bool IsCumulative => ChkCumulative?.IsChecked == true;
    public Task CumulativeLoading { get; private set; } = Task.CompletedTask;

    private void CancelCumulativeBuild()
    {
        _cumulativeCancellation?.Cancel();
        _cumulativeCancellation = null;
    }
    private void ResetCumulativeData()
    {
        _cumulativeVersion++;
        CancelCumulativeBuild();
        CumulativeRows.ItemsSource = null;
        _selectedCumulative = null; _cumulativeData = null;
    }
    private void CumulativeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null || CumulativeRows == null || _changingMapView) return;
        if (IsCumulative)
        {
            _changingMapView = true;
            try { ChkStacked.IsChecked = false; }
            finally { _changingMapView = false; }
        }
        else { CancelCumulativeBuild(); CumulativeRows.ItemsSource = null; }
        ApplyMapView();
    }

    private void StartCumulativeBuild()
    {
        if (!_ready || !IsCumulative) return;
        if (_cumulativeData != null)
        {
            if (CumulativeRows.ItemsSource == null)
                CumulativeRows.ItemsSource = new ListCollectionView(_cumulativeData.Categories.Chunk(4).ToArray());
            RefreshCumulativeLegend();
            return;
        }
        if (_cumulativeCancellation != null) return;
        var cancel = new CancellationTokenSource();
        _cumulativeCancellation = cancel;
        BtnCopyMapTable.IsEnabled = false;
        TxtMapInfo.Text = $"正在累计 {_stackPages.Count:N0} 片晶圆…";
        CumulativeLoading = LoadCumulativeAsync(_stackPages, _cumulativeVersion, cancel);
    }
    private async Task LoadCumulativeAsync(IReadOnlyList<Models.WaferMapPage> pages, long version, CancellationTokenSource cancel)
    {
        try
        {
            var data = await Task.Run(() => CumulativeMapService.Build(pages, _mapTableKind, cancel.Token), cancel.Token);
            if (!_ready || cancel.IsCancellationRequested || version != _cumulativeVersion) return;
            _cumulativeData = data;
            _selectedCumulative = data.Categories.FirstOrDefault();
            CumulativeRows.ItemsSource = new ListCollectionView(data.Categories.Chunk(4).ToArray());
            if (IsCumulative) { RefreshCumulativeLegend(); RefreshVisibleCumulativeTiles(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_ready && IsCumulative && version == _cumulativeVersion)
                TxtMapInfo.Text = "累计失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cumulativeCancellation, cancel)) _cumulativeCancellation = null;
            cancel.Dispose();
        }
    }

    private void RefreshCumulativeLegend()
    {
        ClearLegend();
        if (_cumulativeData is { } data)
        {
            _legend = data.Categories.Select(category => new LegendRow
            {
                Category = category.Category, Count = category.Count, Percent = category.Percent,
                Color = category.Color, IsDisplayed = !_dimmedBins.Contains(category.Category)
            }).ToList();
            if (_isBinMap) foreach (var row in _legend) row.PropertyChanged += LegendVisibility_Changed;
            TxtMapInfo.Text = $"{data.WaferCount:N0} 片有数据晶圆 · {data.CoordinateCount:N0} 个坐标\n{CumulativeMapData.Basis}" +
                (_selectedCumulative == null ? "\n没有可累计的坐标。" : "\n当前选中：" + _selectedCumulative.Title) +
                (data.UnclassifiedCount > 0 ? $"\n无效测量 {data.UnclassifiedCount:N0} 次计入分母，不计 P / F。" : "");
        }
        DgLegend.ItemsSource = new ListCollectionView(_legend);
        BtnCopyMapTable.IsEnabled = _selectedCumulative != null;
    }
    private void CumulativeTile_Loaded(object sender, RoutedEventArgs e)
    { if (sender is WaferMapTile tile) ApplyCumulativeTileOptions(tile); }
    private void CumulativeTile_Selected(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WaferMapTile { CumulativeSource: { } category }) return;
        _selectedCumulative = category;
        TxtCopyStatus.Text = "";
        RefreshCumulativeLegend(); RefreshVisibleCumulativeTiles();
        e.Handled = true;
    }
    private void RefreshVisibleCumulativeTiles()
    {
        if (!_ready || !IsCumulative) return;
        foreach (var tile in VisualChildren<WaferMapTile>(CumulativeRows)) ApplyCumulativeTileOptions(tile);
    }
    private void ApplyCumulativeTileOptions(WaferMapTile tile)
        => tile.SetOptions(new(false, null, Map.DimmedCategories, false, false),
            ReferenceEquals(tile.CumulativeSource, _selectedCumulative));
}
