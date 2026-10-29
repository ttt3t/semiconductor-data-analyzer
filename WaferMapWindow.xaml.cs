using System.Text;
using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class WaferMapWindow : Window
{
    public sealed class LegendRow : INotifyPropertyChanged
    {
        public string Category { get; set; } = "";
        public int Count { get; set; }
        public double Percent { get; set; }
        public Brush Color { get; set; } = Brushes.Gray;
        private bool _isDisplayed = true;
        public bool IsDisplayed
        {
            get => _isDisplayed;
            set
            {
                if (_isDisplayed == value) return;
                _isDisplayed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDisplayed)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private List<LegendRow> _legend = new();
    private readonly bool _isBinMap;
    private readonly MapTableKind _mapTableKind;
    private readonly HashSet<string> _dimmedBins = new(StringComparer.Ordinal);
    private bool _updatingBinVisibility;
    private IList<WaferDie> _pfDies = Array.Empty<WaferDie>();
    private HeatmapScale? _heatmapScale;
    private string _unit = "";
    private IList<WaferDie>? _heatDies;
    private bool _ready;
    private bool _useSigmaRange = true, _useCustomRange, _updatingHeatRange;
    private double _heatSigma = 6;
    private (double Blue, double Green, double Red)? _customRange;

    public WaferMapWindow(string title, IList<WaferDie> dies, HeatmapScale? heatmapScale = null, string unit = "")
        : this(title, new[] { new WaferMapPage(dies.FirstOrDefault()?.WaferId ?? "Wafer", dies, heatmapScale, unit, heatmapScale != null) })
    { }

    public WaferMapWindow(string title, IReadOnlyList<WaferMapPage> pages, string? binName = null)
    {
        InitializeComponent();
        _isBinMap = binName != null;
        _mapTableKind = binName == null ? MapTableKind.PassFail :
            binName.Equals("HBIN", StringComparison.OrdinalIgnoreCase) ? MapTableKind.HBin : MapTableKind.SBin;
        if (binName != null)
        {
            BinVisibilityColumn.Visibility = Visibility.Visible;
            ChkBinNumbers.Content = $"显示 {binName} 数字";
            ChkBinNumbers.Visibility = Visibility.Visible;
        }
        Map.HoverChanged += ShowHoverInfo;
        Deactivated += (_, _) => Map.ClearHover();
        Closed += (_, _) =>
        {
            Map.HoverChanged -= ShowHoverInfo;
            HoverInfo.Visibility = Visibility.Collapsed;
            HoverText.Text = "";
            _ready = false;
            BtnCopyMapTable.IsEnabled = false;
            Map.Dies = null;
            WaferList.ItemsSource = null;
            DgLegend.ItemsSource = null;
            _pfDies = Array.Empty<WaferDie>();
            _heatDies = null;
            _heatmapScale = null;
            ClearLegend();
            _dimmedBins.Clear();
            Map.DimmedCategories = null;
        };
        Title = title;
        TxtTitle.Text = title;
        SetPages(pages);
        var layout = new WindowLayoutPersistence(this, "WaferMap");
        layout.TrackTable("Legend", DgLegend);
        // Map types share their saved layout, but this option belongs to BIN maps only.
        BinVisibilityColumn.Visibility = _isBinMap ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHoverInfo(string text, Point position)
    {
        if (text.Length == 0)
        {
            HoverInfo.Visibility = Visibility.Collapsed;
            HoverText.Text = "";
            return;
        }
        HoverText.Text = text;
        HoverInfo.MaxWidth = Math.Max(1, Map.ActualWidth - 8);
        HoverInfo.MaxHeight = Math.Max(1, Map.ActualHeight - 8);
        HoverInfo.Visibility = Visibility.Visible;
        HoverInfo.Measure(new Size(HoverInfo.MaxWidth, HoverInfo.MaxHeight));
        var size = HoverInfo.DesiredSize;
        double x = position.X + 14, y = position.Y + 16;
        if (x + size.Width > Map.ActualWidth - 4) x = position.X - size.Width - 14;
        if (y + size.Height > Map.ActualHeight - 4) y = position.Y - size.Height - 16;
        Canvas.SetLeft(HoverInfo, Math.Clamp(x, 4, Math.Max(4, Map.ActualWidth - size.Width - 4)));
        Canvas.SetTop(HoverInfo, Math.Clamp(y, 4, Math.Max(4, Map.ActualHeight - size.Height - 4)));
    }

    public void SetPages(IReadOnlyList<WaferMapPage> pages)
    {
        string? selected = (WaferList.SelectedItem as WaferMapPage)?.WaferId;
        _ready = false;
        // Own the view instead of leaving a default view cached for this snapshot.
        var snapshot = pages.ToList();
        WaferList.ItemsSource = new ListCollectionView(snapshot);
        int index = snapshot.FindIndex(p => p.WaferId == selected);
        WaferList.SelectedIndex = index >= 0 ? index : pages.Count > 0 ? 0 : -1;
        if (WaferList.SelectedItem != null) WaferList.ScrollIntoView(WaferList.SelectedItem);
        _ready = true;
        ApplySelectedPage();
        if (pages.Count == 0)
        {
            BtnCopyMapTable.IsEnabled = false;
            TxtCopyStatus.Text = "";
            Map.Dies = null; _pfDies = Array.Empty<WaferDie>(); _heatDies = null; _heatmapScale = null;
            ClearLegend(); DgLegend.ItemsSource = new ListCollectionView(_legend);
            TxtMapInfo.Text = "没有可显示的 wafer。"; TxtHeatRange.Text = "";
            ModePanel.Visibility = HeatLegend.Visibility = Visibility.Collapsed;
            DgLegend.Visibility = Visibility.Visible;
        }
    }

    private void Wafer_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ApplySelectedPage();

    public void SelectWafer(string wafer)
    {
        var page = WaferList.Items.OfType<WaferMapPage>().FirstOrDefault(p => p.WaferId == wafer);
        if (page != null) WaferList.SelectedItem = page;
    }

    private void ApplySelectedPage()
    {
        if (!_ready || WaferList.SelectedItem is not WaferMapPage page) return;
        BtnCopyMapTable.IsEnabled = page.Dies.Count > 0;
        TxtCopyStatus.Text = "";
        if (_customRange is { } previous && _unit != page.Unit)
        {
            if (MeasurementDisplay.ConversionFactor(_unit, page.Unit) is double factor &&
                double.IsFinite(previous.Blue * factor) && double.IsFinite(previous.Green * factor) && double.IsFinite(previous.Red * factor))
            {
                _customRange = (previous.Blue * factor, previous.Green * factor, previous.Red * factor);
                TxtHeatBlue.Text = _customRange.Value.Blue.ToString("G17", CultureInfo.InvariantCulture);
                TxtHeatGreen.Text = _customRange.Value.Green.ToString("G17", CultureInfo.InvariantCulture);
                TxtHeatRed.Text = _customRange.Value.Red.ToString("G17", CultureInfo.InvariantCulture);
            }
            else
            {
                _customRange = null; _useCustomRange = false; _useSigmaRange = true;
                _updatingHeatRange = true;
                SigmaRangeMode.IsChecked = true; CustomRangeMode.IsChecked = MinMaxRangeMode.IsChecked = false;
                _updatingHeatRange = false;
            }
        }
        _pfDies = page.Dies;
        _heatmapScale = _useCustomRange && _customRange is { } custom
            ? page.HeatmapScale?.WithCustomRange(custom.Blue, custom.Green, custom.Red)
            : page.HeatmapScale?.WithSigmaRange(_useSigmaRange ? _heatSigma : null);
        if (_customRange == null && _heatmapScale != null)
        {
            TxtHeatBlue.Text = _heatmapScale.Minimum.ToString("G17", CultureInfo.InvariantCulture);
            TxtHeatGreen.Text = _heatmapScale.Center.ToString("G17", CultureInfo.InvariantCulture);
            TxtHeatRed.Text = _heatmapScale.Maximum.ToString("G17", CultureInfo.InvariantCulture);
        }
        _unit = page.Unit; _heatDies = null;
        ModePanel.Visibility = page.AllowHeatmap ? Visibility.Visible : Visibility.Collapsed;
        HeatMode.IsEnabled = _heatmapScale != null;
        if (_heatmapScale == null) PfMode.IsChecked = true;
        TxtHeatRange.Text = HeatRangeText();
        TxtMapInfo.Text = page.Dies.Count == 0 ? "此 wafer 在当前测试项 / Site 下没有可绘制坐标。" : $"{page.Dies.Count} 颗芯片";
        int total = Math.Max(1, _pfDies.Count);
        ClearLegend();
        _legend = _pfDies
            .GroupBy(d => d.Category)
            .Select(g => new LegendRow
            {
                Category = g.Key,
                Count = g.Count(),
                Percent = 100.0 * g.Count() / total,
                Color = g.First().Color,
                IsDisplayed = !_dimmedBins.Contains(g.Key)
            })
            .OrderBy(r => ParseBinSortKey(r.Category))
            .ThenBy(r => r.Category)
            .ToList();

        if (_isBinMap)
            foreach (var row in _legend) row.PropertyChanged += LegendVisibility_Changed;
        DgLegend.ItemsSource = new ListCollectionView(_legend);
        MapMode_Changed(this, new RoutedEventArgs());
    }

    private void ClearLegend()
    {
        foreach (var row in _legend) row.PropertyChanged -= LegendVisibility_Changed;
        _legend.Clear();
    }

    private void LegendVisibility_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready || !_isBinMap || _updatingBinVisibility || sender is not LegendRow row || e.PropertyName != nameof(LegendRow.IsDisplayed)) return;
        if (row.IsDisplayed) _dimmedBins.Remove(row.Category);
        else _dimmedBins.Add(row.Category);
        ApplyBinVisibility();
    }

    private void BinVisibilityHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || !_isBinMap || _legend.Count == 0) return;
        // A mixed selection becomes all checked; the next click becomes all unchecked.
        bool show = _legend.Any(row => !row.IsDisplayed);
        _updatingBinVisibility = true;
        try
        {
            foreach (var row in _legend)
            {
                row.IsDisplayed = show;
                if (show) _dimmedBins.Remove(row.Category);
                else _dimmedBins.Add(row.Category);
            }
        }
        finally { _updatingBinVisibility = false; }
        ApplyBinVisibility();
        e.Handled = true;
    }

    private void ApplyBinVisibility()
    {
        // Replace a small set once per click, rather than copying dies or rebuilding the wafer.
        Map.DimmedCategories = _dimmedBins.Count == 0 ? null : new HashSet<string>(_dimmedBins, StringComparer.Ordinal);
    }

    private void HeatRangeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingHeatRange) return;
        bool sigma = SigmaRangeMode.IsChecked == true;
        bool custom = CustomRangeMode.IsChecked == true;
        double multiple = _heatSigma;
        string error = "";
        var range = _customRange;
        if (custom)
        {
            if (!DatasetService.TryValue(TxtHeatBlue.Text, out double blue) ||
                !DatasetService.TryValue(TxtHeatGreen.Text, out double green) ||
                !DatasetService.TryValue(TxtHeatRed.Text, out double red) || blue >= green || green >= red)
                error = "色标须为有限数字，且蓝标 < 绿标 < 红标。";
            else range = (blue, green, red);
        }
        else if (sigma && (!double.TryParse(TxtHeatSigma.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out multiple)
            || !double.IsFinite(multiple) || multiple <= 0))
            error = "N 必须为大于 0 的有限数字。";
        if (error.Length > 0)
        {
            TxtHeatError.Text = error;
            _updatingHeatRange = true;
            SigmaRangeMode.IsChecked = _useSigmaRange;
            MinMaxRangeMode.IsChecked = !_useSigmaRange && !_useCustomRange;
            CustomRangeMode.IsChecked = _useCustomRange;
            _updatingHeatRange = false;
            return;
        }
        _useSigmaRange = sigma; _useCustomRange = custom; _customRange = range;
        _heatSigma = multiple; TxtHeatError.Text = "";
        ApplySelectedPage();
    }

    private void ApplyCustomRange_Click(object sender, RoutedEventArgs e)
    {
        _updatingHeatRange = true;
        CustomRangeMode.IsChecked = true;
        _updatingHeatRange = false;
        HeatRangeMode_Changed(sender, e);
    }

    private string HeatRangeText() => _heatmapScale == null ? "" :
        (WaferList.Items.Count > 1 ? "所有 wafer 共用色标（当前 Site）\n" : "") +
        (_useCustomRange ? "色标：自定义\n" : _useSigmaRange ? $"色标：均值 ± {_heatSigma:G}σ\n" : "色标：最小值 / 均值 / 最大值\n") +
        $"蓝色：{ChartAxis.Format(_heatmapScale.Minimum)} {_unit}\n" +
        $"绿色{(_useCustomRange ? "" : "（均值）")}：{ChartAxis.Format(_heatmapScale.Center)} {_unit}\n" +
        $"红色：{ChartAxis.Format(_heatmapScale.Maximum)} {_unit}\n" +
        (_useCustomRange ? $"实际均值：{ChartAxis.Format(_heatmapScale.Mean)} {_unit}\n" : "") +
        $"σ：{ChartAxis.Format(_heatmapScale.Sigma)} {_unit}";

    private void MapMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null || HeatLegend == null || DgLegend == null) return;
        bool heat = HeatMode.IsChecked == true && _heatmapScale != null;
        if (heat && _heatDies == null)
            _heatDies = _pfDies.Select(d => d.WithColor(_heatmapScale!.ColorForValue(d.Value))).ToList();
        Map.Dies = heat ? _heatDies : _pfDies;
        HeatLegend.Visibility = heat ? Visibility.Visible : Visibility.Collapsed;
        DgLegend.Visibility = heat ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int ParseBinSortKey(string category)
    {
        return int.TryParse(category, out int n) ? n : int.MaxValue;
    }

    private void BtnCopyLegend_Click(object sender, RoutedEventArgs e)
    {
        if (HeatMode.IsChecked == true && _heatmapScale != null)
        {
            CopyLegendText("热力图色标\n" + HeatRangeText());
            return;
        }
        if (_legend.Count == 0)
        {
            MessageBox.Show("没有可复制的图例", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Bin\t数量\t占比%");
        foreach (var r in _legend)
        {
            sb.Append(r.Category).Append('\t')
              .Append(r.Count).Append('\t')
              .Append(r.Percent.ToString("F2"))
              .AppendLine();
        }

        CopyLegendText(sb.ToString());
    }

    private void BtnCopyMapTable_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || WaferList.SelectedItem is not WaferMapPage page || _pfDies.Count == 0)
        { TxtCopyStatus.Text = "没有可复制的 Map 数据"; return; }
        try
        {
            // Copy the selected page's classifications, including dimmed BINs and P/F in heat mode.
            // No matrix or extra numeric columns are retained during rendering/wafer switching.
            var table = MapTableCopyService.Build(_pfDies.Select(d =>
                new MapTableEntry(d.X, d.Y, d.SBin, d.HBin, d.Category)), _mapTableKind);
            string status = $"已复制 {page.WaferId} 的 {table.Columns}×{table.Rows} Map 表格（{table.DieCount} 颗芯片）";
            if (table.ErrorCount > 0) status += $"；{table.ErrorCount} 颗无效测量标为 E";
            CopyText(table.Text, status);
        }
        catch (InvalidOperationException ex) { TxtCopyStatus.Text = ex.Message; }
    }

    private void CopyLegendText(string text) => CopyText(text, "已复制");

    private void CopyText(string text, string success)
    {
        try { Clipboard.SetText(text); TxtCopyStatus.Text = success; }
        catch (System.Runtime.InteropServices.ExternalException) { TxtCopyStatus.Text = "剪贴板正忙，请重试"; }
    }

    public static Brush ColorForCategory(string category)
    {
        if (category.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            return PassBrush;
        if (category.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            return FailBrush;
        if (category.Equals("Eorr", StringComparison.OrdinalIgnoreCase)) return Brushes.Gray;
        if (string.IsNullOrWhiteSpace(category) || category == "(空)")
            return Brushes.Gray;

        return BinPalette.ColorForCategory(category);
    }

    private static readonly Brush PassBrush = NewBrush(46, 160, 67);
    private static readonly Brush FailBrush = NewBrush(210, 60, 60);
    private static SolidColorBrush NewBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
