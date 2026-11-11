using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public sealed record WaferMapTileOptions(bool Heat, HeatmapScale? Scale, IReadOnlySet<string>? DimmedBins, bool Numbers, bool SiteBorders);

public partial class WaferMapTile : UserControl
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(nameof(Page), typeof(WaferMapPage), typeof(WaferMapTile),
        new PropertyMetadata(null, (d, _) => ((WaferMapTile)d).ApplyPage()));
    public WaferMapPage? Page { get => (WaferMapPage?)GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public static readonly DependencyProperty CumulativeSourceProperty = DependencyProperty.Register(nameof(CumulativeSource), typeof(CumulativeMapCategory), typeof(WaferMapTile),
        new PropertyMetadata(null, (d, _) => ((WaferMapTile)d).ApplyCumulativeSource()));
    public CumulativeMapCategory? CumulativeSource { get => (CumulativeMapCategory?)GetValue(CumulativeSourceProperty); set => SetValue(CumulativeSourceProperty, value); }
    private WaferMapPage? _cumulativePage;
    public WaferMapPage? DisplayPage => _cumulativePage ?? Page;
    private WaferMapTileOptions _options = new(false, null, null, false, false);
    private static readonly Brush SelectedBorder = Brushes.RoyalBlue;
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    static WaferMapTile() => NormalBorder.Freeze();

    public WaferMapTile()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (CumulativeSource != null) ApplyCumulativeSource(); };
        Unloaded += (_, _) =>
        {
            TileMap.Dies = null; TileMap.ColorScale = null; TileMap.DimmedCategories = null;
            _cumulativePage = null; TileHover.Visibility = Visibility.Collapsed;
        };
        TileMap.HoverChanged += ShowCumulativeHover;
    }
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Width)) TileMap.Height = Math.Max(40, constraint.Width - 8);
        return base.MeasureOverride(constraint);
    }
    public void SetOptions(WaferMapTileOptions options, bool selected)
    {
        _options = options;
        Frame.BorderBrush = selected ? SelectedBorder : NormalBorder;
        ApplyOptions();
        // A recycled row can be reloaded with the same page instance.
        if (!ReferenceEquals(TileMap.Dies, DisplayPage?.Dies)) TileMap.Dies = DisplayPage?.Dies;
    }
    private void ApplyCumulativeSource()
    {
        _cumulativePage = IsLoaded ? CumulativeSource?.CreatePage() : null;
        ApplyPage();
    }
    private void ApplyPage()
    {
        if (TileMap == null) return;
        var page = DisplayPage;
        TitleText.Text = page?.DisplayName ?? "";
        TitleText.ToolTip = page?.DisplayName;
        YieldText.Text = !string.IsNullOrEmpty(page?.Summary) ? page.Summary : page?.PassCount is int good && page.Dies.Count > 0
            ? $"良率 {good * 100.0 / page.Dies.Count:F2}%" : "良率 —";
        YieldText.ToolTip = !string.IsNullOrEmpty(page?.SummaryDetails) ? page.SummaryDetails : page?.PassCount is int pass
            ? $"{page.YieldBasis}\n良品 {pass:N0} / {page.Dies.Count:N0}" : page?.YieldBasis;
        TileMap.Dies = page?.Dies;
        ApplyOptions();
    }
    private void ApplyOptions()
    {
        var desired = CumulativeSource == null && _options.Heat ? _options.Scale : null;
        var current = TileMap.ColorScale;
        if (current?.Minimum != desired?.Minimum || current?.Center != desired?.Center || current?.Maximum != desired?.Maximum)
            TileMap.ColorScale = desired;
        TileMap.DimmedCategories = _options.DimmedBins;
        TileMap.ShowBinNumbers = CumulativeSource == null && _options.Numbers;
        TileMap.ShowSiteBorders = CumulativeSource == null && _options.SiteBorders;
    }

    private void ShowCumulativeHover(string text, Point point)
    {
        if (CumulativeSource == null || text.Length == 0)
        { TileHover.Visibility = Visibility.Collapsed; return; }
        // Reuse the in-window overlay instead of creating a native tooltip for every coordinate.
        int detail = text.IndexOf(CumulativeSource.Title, StringComparison.Ordinal);
        TileHoverText.Text = detail >= 0 ? text[detail..] : text;
        TileHover.MaxWidth = Math.Max(1, TileMap.ActualWidth - 4);
        TileHover.Visibility = Visibility.Visible;
        TileHover.Measure(new Size(TileHover.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(TileHover, Math.Clamp(point.X + 6, 0, Math.Max(0, TileMap.ActualWidth - TileHover.DesiredSize.Width)));
        Canvas.SetTop(TileHover, Math.Clamp(point.Y + 6, 0, Math.Max(0, TileMap.ActualHeight - TileHover.DesiredSize.Height)));
    }
}
