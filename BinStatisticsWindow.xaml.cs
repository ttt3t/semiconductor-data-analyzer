using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class BinStatisticsWindow : Window
{
    private BinStatisticsResult? _first, _last;
    private readonly List<string> _sites;
    public BinStatisticsWindow(BinStatisticsResult first, BinStatisticsResult last)
    {
        InitializeComponent();
        _first = first; _last = last;
        _sites = first.Sites.Concat(last.Sites).Distinct().OrderBy(NumericText.SortKey).ThenBy(s => s, StringComparer.Ordinal).ToList();
        for (int i = 0; i < _sites.Count; i++)
            DgSites.Columns.Add(new DataGridTextColumn
            {
                Header = $"Site {_sites[i]}\n占比%", Width = 110, CanUserSort = false,
                Binding = new Binding($"Percentages[{i}]") { Mode = BindingMode.OneTime, StringFormat = "F2", TargetNullValue = "–" }
            });
        var colors = new CategoryBrushConverter(first.Categories.Concat(last.Categories).Select(r => r.Category));
        var categoryStyle = new Style(typeof(DataGridCell));
        categoryStyle.Setters.Add(new Setter(BackgroundProperty,
            new Binding(nameof(BinCountRow.Category)) { Mode = BindingMode.OneTime, Converter = colors }));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        selected.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        categoryStyle.Triggers.Add(selected);
        DgSbin.Columns[0].CellStyle = DgCategories.Columns[0].CellStyle = DgSites.Columns[0].CellStyle = categoryStyle;
        Mode_Changed(this, new RoutedEventArgs());
        var layout = new WindowLayoutPersistence(this, "BinStatistics");
        layout.TrackTable("SBINCategories", DgSbin); layout.TrackTable("HBIN", DgHbin); layout.TrackTable("Categories", DgCategories);
        layout.TrackTable("SBINSites", DgSites); layout.TrackRows("SummaryAndSites", SummaryTablesRow, SiteTableRow);
        Closed += (_, _) =>
        {
            DgSbin.ItemsSource = DgHbin.ItemsSource = DgCategories.ItemsSource = DgSites.ItemsSource = null;
            _first = _last = null;
        };
    }

    internal sealed class CategoryBrushConverter : IValueConverter
    {
        private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
        public CategoryBrushConverter(IEnumerable<string> categories)
        {
            foreach (string name in categories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // One frozen pastel per category, shared by both tables and modes.
                double hue = (210 + _brushes.Count * 137.508) % 360 / 60;
                double x = 50 * (1 - Math.Abs(hue % 2 - 1));
                (double r, double g, double b) = hue switch
                {
                    < 1 => (50d, x, 0d), < 2 => (x, 50d, 0d), < 3 => (0d, 50d, x),
                    < 4 => (0d, x, 50d), < 5 => (x, 0d, 50d), _ => (50d, 0d, x)
                };
                var brush = new SolidColorBrush(name == "未分类" ? Color.FromRgb(235, 235, 235)
                    : Color.FromRgb((byte)(205 + r), (byte)(205 + g), (byte)(205 + b)));
                brush.Freeze();
                _brushes.Add(name, brush);
            }
        }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string category && _brushes.TryGetValue(category, out var brush) ? brush : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        var result = FirstMode.IsChecked == true ? _first : _last;
        if (result == null) return;
        TxtCopyStatus.Text = "";
        DgSbin.ItemsSource = new ListCollectionView(result.SBins);
        DgHbin.ItemsSource = new ListCollectionView(result.HBins);
        DgCategories.ItemsSource = new ListCollectionView(result.Categories);
        var indices = _sites.Select(s => result.Sites.IndexOf(s)).ToArray();
        var siteRows = result.SiteRows.Select(r => new BinSiteRow(r.Category, r.Bin,
            indices.Select(i => i >= 0 ? r.Percentages[i] : null).ToArray())).ToList();
        DgSites.ItemsSource = new ListCollectionView(siteRows);
        TxtInfo.Text = $"晶圆内去重芯片：{result.ChipCount}  |  无 SBIN：{result.MissingSBin}  |  无 HBIN：{result.MissingHBin}  |  " +
            $"Pass：{100.0 * result.PassCount / Math.Max(1, result.ChipCount):F2}%  |  Pass + PartialPass：{100.0 * (result.PassCount + result.PartialPassCount) / Math.Max(1, result.ChipCount):F2}%";
    }

    private string BuildClipboardText()
    {
        var text = new StringBuilder();
        // Quote tabs/newlines so custom category names remain a single Excel cell.
        static string Cell(string value) => value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        void Row(params string[] cells) => text.AppendLine(string.Join("\t", cells.Select(Cell)));
        static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
        static string Percent(double value) => value.ToString("F2", CultureInfo.CurrentCulture);
        Row("复测模式", FirstMode.IsChecked == true ? "只保留首测" : "复测覆盖首测");
        text.AppendLine();
        Row("SBIN 明细");
        Row("分类", "SBIN", "数量", "占比%");
        // Read the entire sorted view, not just selected/realized cells.
        foreach (var row in DgSbin.Items.OfType<BinCountRow>())
            Row(row.Category, row.Bin, Number(row.Count), Percent(row.Percent));
        text.AppendLine();
        Row("HBIN 明细");
        Row("HBIN", "数量", "占比%");
        foreach (var row in DgHbin.Items.OfType<BinCountRow>())
            Row(row.Bin, Number(row.Count), Percent(row.Percent));
        text.AppendLine();
        Row("SBIN 分类汇总");
        Row("分类", "数量", "占比%");
        foreach (var row in DgCategories.Items.OfType<BinCategoryRow>())
            Row(row.Category, Number(row.Count), Percent(row.Percent));
        text.AppendLine();
        Row("SBIN 各 Site 占比");
        Row(new[] { "分类", "SBIN" }.Concat(_sites.Select(s => $"Site {s} 占比%" )).ToArray());
        foreach (var row in DgSites.Items.OfType<BinSiteRow>())
            Row(new[] { row.Category, row.Bin }.Concat(row.Percentages.Select(p => p.HasValue ? Percent(p.Value) : "–")).ToArray());
        return text.ToString();
    }

    private void DgSbin_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {

    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildClipboardText());
            TxtCopyStatus.Text = "已复制";
        }
        catch (COMException) { TxtCopyStatus.Text = "剪贴板正忙，请重试"; }
    }
}
