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
    private WaferMapTileOptions _options = new(false, null, null, false, false);
    private static readonly Brush SelectedBorder = Brushes.RoyalBlue;
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    static WaferMapTile() => NormalBorder.Freeze();

    public WaferMapTile()
    {
        InitializeComponent();
        Unloaded += (_, _) => { TileMap.Dies = null; TileMap.ColorScale = null; TileMap.DimmedCategories = null; };
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
        if (!ReferenceEquals(TileMap.Dies, Page?.Dies)) TileMap.Dies = Page?.Dies;
    }
    private void ApplyPage()
    {
        if (TileMap == null) return;
        var page = Page;
        TitleText.Text = page?.WaferId ?? "";
        TitleText.ToolTip = page?.WaferId;
        YieldText.Text = page?.PassCount is int good && page.Dies.Count > 0
            ? $"良率 {good * 100.0 / page.Dies.Count:F2}%" : "良率 —";
        YieldText.ToolTip = page?.PassCount is int pass
            ? $"{page.YieldBasis}\n良品 {pass:N0} / {page.Dies.Count:N0}" : page?.YieldBasis;
        TileMap.Dies = page?.Dies;
        ApplyOptions();
    }
    private void ApplyOptions()
    {
        var desired = _options.Heat ? _options.Scale : null;
        var current = TileMap.ColorScale;
        if (current?.Minimum != desired?.Minimum || current?.Center != desired?.Center || current?.Maximum != desired?.Maximum)
            TileMap.ColorScale = desired;
        TileMap.DimmedCategories = _options.DimmedBins;
        TileMap.ShowBinNumbers = _options.Numbers;
        TileMap.ShowSiteBorders = _options.SiteBorders;
    }
}
