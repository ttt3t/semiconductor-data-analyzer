using System.Text;
using System.Globalization;
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
    public sealed class LegendRow
    {
        public string Category { get; set; } = "";
        public int Count { get; set; }
        public double Percent { get; set; }
        public Brush Color { get; set; } = Brushes.Gray;
    }

    private List<LegendRow> _legend = new();
    private IList<WaferDie> _pfDies = Array.Empty<WaferDie>();
    private HeatmapScale? _heatmapScale;
    private string _unit = "";
    private IList<WaferDie>? _heatDies;
    private bool _ready;
    private bool _useSigmaRange = true, _updatingHeatRange;
    private double _heatSigma = 6;

    public WaferMapWindow(string title, IList<WaferDie> dies, HeatmapScale? heatmapScale = null, string unit = "")
        : this(title, new[] { new WaferMapPage(dies.FirstOrDefault()?.WaferId ?? "Wafer", dies, heatmapScale, unit, heatmapScale != null) })
    { }

    public WaferMapWindow(string title, IReadOnlyList<WaferMapPage> pages, string? binName = null)
    {
        InitializeComponent();
        if (binName != null)
        {
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
            Map.Dies = null;
            WaferList.ItemsSource = null;
            DgLegend.ItemsSource = null;
            _pfDies = Array.Empty<WaferDie>();
            _heatDies = null;
            _heatmapScale = null;
            _legend.Clear();
        };
        Title = title;
        TxtTitle.Text = title;
        SetPages(pages);
        var layout = new WindowLayoutPersistence(this, "WaferMap");
        layout.TrackTable("Legend", DgLegend);
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
            Map.Dies = null; _pfDies = Array.Empty<WaferDie>(); _heatDies = null; _heatmapScale = null;
            _legend = new(); DgLegend.ItemsSource = new ListCollectionView(_legend);
            TxtMapInfo.Text = "没有可显示的 wafer。"; TxtHeatRange.Text = "";
            ModePanel.Visibility = HeatLegend.Visibility = Visibility.Collapsed;
            DgLegend.Visibility = Visibility.Visible;
        }
    }

    private void Wafer_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ApplySelectedPage();

    private void ApplySelectedPage()
    {
        if (!_ready || WaferList.SelectedItem is not WaferMapPage page) return;
        _pfDies = page.Dies; _heatmapScale = page.HeatmapScale?.WithSigmaRange(_useSigmaRange ? _heatSigma : null);
        _unit = page.Unit; _heatDies = null;
        ModePanel.Visibility = page.AllowHeatmap ? Visibility.Visible : Visibility.Collapsed;
        HeatMode.IsEnabled = _heatmapScale != null;
        if (_heatmapScale == null) PfMode.IsChecked = true;
        TxtHeatRange.Text = HeatRangeText();
        TxtMapInfo.Text = page.Dies.Count == 0 ? "此 wafer 在当前测试项 / Site 下没有可绘制坐标。" : $"{page.Dies.Count} 颗芯片；悬停查看详细信息";
        int total = Math.Max(1, _pfDies.Count);
        _legend = _pfDies
            .GroupBy(d => d.Category)
            .Select(g => new LegendRow
            {
                Category = g.Key,
                Count = g.Count(),
                Percent = 100.0 * g.Count() / total,
                Color = g.First().Color
            })
            .OrderBy(r => ParseBinSortKey(r.Category))
            .ThenBy(r => r.Category)
            .ToList();

        DgLegend.ItemsSource = new ListCollectionView(_legend);
        MapMode_Changed(this, new RoutedEventArgs());
    }

    private void HeatRangeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingHeatRange) return;
        bool sigma = SigmaRangeMode.IsChecked == true;
        double multiple = _heatSigma;
        if (sigma && (!double.TryParse(TxtHeatSigma.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out multiple)
            || !double.IsFinite(multiple) || multiple <= 0))
        {
            TxtHeatError.Text = "N 必须为大于 0 的有限数字。";
            _updatingHeatRange = true;
            SigmaRangeMode.IsChecked = _useSigmaRange; MinMaxRangeMode.IsChecked = !_useSigmaRange;
            _updatingHeatRange = false;
            return;
        }
        _useSigmaRange = sigma; _heatSigma = multiple; TxtHeatError.Text = "";
        ApplySelectedPage();
    }

    private string HeatRangeText() => _heatmapScale == null ? "" :
        (WaferList.Items.Count > 1 ? "所有 wafer 共用色标（当前 Site）\n" : "") +
        (_useSigmaRange ? $"色标：均值 ± {_heatSigma:G}σ\n" : "色标：最小值 / 均值 / 最大值\n") +
        $"蓝色：{ChartAxis.Format(_heatmapScale.Minimum)} {_unit}\n" +
        $"均值：{ChartAxis.Format(_heatmapScale.Mean)} {_unit}\n" +
        $"红色：{ChartAxis.Format(_heatmapScale.Maximum)} {_unit}\n" +
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
            Clipboard.SetText("热力图色标\n" + HeatRangeText());
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

        Clipboard.SetText(sb.ToString());
    }

    public static Brush ColorForCategory(string category)
    {
        if (category.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            return PassBrush;
        if (category.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            return FailBrush;
        if (category.Equals("Eorr", StringComparison.OrdinalIgnoreCase)) return Brushes.Gray;
        if (IsBinOne(category))
            return PassBrush;
        if (string.IsNullOrWhiteSpace(category) || category == "(空)")
            return Brushes.Gray;

        return ColorFromPalette(category);
    }

    private static bool IsBinOne(string category)
    {
        var s = category.Trim();
        if (s.Equals("1", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("01", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("BIN1", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("SBIN1", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("HBIN1", StringComparison.OrdinalIgnoreCase)) return true;
        return double.TryParse(s, out double n) && Math.Abs(n - 1) < 1e-9;
    }

    private static readonly Brush PassBrush = NewBrush(46, 160, 67);
    private static readonly Brush FailBrush = NewBrush(210, 60, 60);
    private static readonly Brush[] Palette = new Color[]
        {
            Color.FromRgb(31, 119, 180),
            Color.FromRgb(255, 127, 14),
            Color.FromRgb(148, 103, 189),
            Color.FromRgb(140, 86, 75),
            Color.FromRgb(227, 119, 194),
            Color.FromRgb(127, 127, 127),
            Color.FromRgb(188, 189, 34),
            Color.FromRgb(23, 190, 207),
            Color.FromRgb(174, 199, 232),
            Color.FromRgb(255, 187, 120),
            Color.FromRgb(197, 176, 213),
            Color.FromRgb(196, 156, 148),
            Color.FromRgb(247, 182, 210),
            Color.FromRgb(199, 199, 199),
            Color.FromRgb(219, 219, 141),
            Color.FromRgb(158, 218, 229),
            Color.FromRgb(214, 39, 40),
            Color.FromRgb(44, 160, 44),
            Color.FromRgb(255, 187, 0),
            Color.FromRgb(0, 128, 128),
        }.Select(c => (Brush)NewBrush(c.R, c.G, c.B)).ToArray();

    private static Brush ColorFromPalette(string category)
    {
        int key = category.GetHashCode();
        if (int.TryParse(category, out int binNo))
            key = binNo;

        return Palette[(int)(Math.Abs((long)key) % Palette.Length)];
    }

    private static SolidColorBrush NewBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
