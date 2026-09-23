using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class WaferTableWindow : Window
{
    public sealed record DisplayRow(string Bin, string Category, double?[] Values)
    {
        public double BinSort => NumericText.SortKey(Bin);
    }
    private WaferStatisticsResult? _first, _last;
    private readonly List<string> _wafers;

    public WaferTableWindow(WaferStatisticsResult first, WaferStatisticsResult last)
    {
        InitializeComponent();
        _first = first; _last = last;
        _wafers = first.Wafers.Concat(last.Wafers).Distinct(StringComparer.Ordinal).ToList();
        foreach (var grid in new[] { DgWafers, DgHBinWafers, DgCategoryWafers })
            for (int i = 0; i < _wafers.Count; i++)
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = _wafers[i], Width = 140, CanUserSort = false,
                    Binding = ValueBinding(i, false)
                });
        var colors = new BinStatisticsWindow.CategoryBrushConverter(first.CategoryRows.Concat(last.CategoryRows)
            .Concat(first.Rows).Concat(last.Rows).Select(r => r.Category));
        var categoryStyle = new Style(typeof(DataGridCell));
        categoryStyle.Setters.Add(new Setter(BackgroundProperty,
            new Binding(nameof(DisplayRow.Category)) { Mode = BindingMode.OneTime, Converter = colors }));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        selected.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        categoryStyle.Triggers.Add(selected);
        DgWafers.Columns[1].CellStyle = categoryStyle;
        DgCategoryWafers.Columns[0].CellStyle = categoryStyle;
        Mode_Changed(this, new RoutedEventArgs());
        var layout = new WindowLayoutPersistence(this, "WaferTable");
        layout.TrackTable("SBINWafers", DgWafers);
        layout.TrackTable("HBINWafers", DgHBinWafers);
        layout.TrackTable("CategoryWafers", DgCategoryWafers);
        layout.TrackColumns("SBINAndOtherTables", SbinPanelColumn, OtherPanelsColumn);
        layout.TrackColumns("HBINAndCategoryTables", HbinPanelColumn, CategoryPanelColumn);
        Closed += (_, _) =>
        {
            DgWafers.ItemsSource = DgHBinWafers.ItemsSource = DgCategoryWafers.ItemsSource = null;
            _first = _last = null;
            _wafers.Clear();
        };
    }

    private static Binding ValueBinding(int index, bool counts) => new($"Values[{index}]")
    { Mode = BindingMode.OneTime, StringFormat = counts ? "F0" : "F2", TargetNullValue = "–" };

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        var result = FirstMode.IsChecked == true ? _first : _last;
        if (result == null) return;
        bool counts = CountMode.IsChecked == true;
        var indices = _wafers.Select(w => result.Wafers.IndexOf(w)).ToArray();
        void Show(DataGrid grid, int fixedColumns, List<WaferBinRow> sourceRows, int[] validCounts, string binName)
        {
            for (int i = 0; i < _wafers.Count; i++)
            {
                int source = indices[i];
                var column = (DataGridTextColumn)grid.Columns[i + fixedColumns];
                column.Header = $"{_wafers[i]}\n有效 {binName}：{(source >= 0 && source < validCounts.Length ? validCounts[source] : 0)}";
                column.Binding = ValueBinding(i, counts);
            }
            var rows = sourceRows.Select(r => new DisplayRow(r.Bin, r.Category, indices.Select(i =>
                i < 0 ? (double?)null : counts ? r.Counts[i] : r.Percentages[i]).ToArray())).ToList();
            grid.ItemsSource = new ListCollectionView(rows);
        }
        Show(DgWafers, 2, result.Rows, result.ValidSBinCounts, "SBIN");
        Show(DgHBinWafers, 1, result.HBinRows, result.ValidHBinCounts, "HBIN");
        Show(DgCategoryWafers, 1, result.CategoryRows, result.ValidSBinCounts, "SBIN");
        TxtInfo.Text = $"Wafer {_wafers.Count} 个  |  去重芯片 {result.ChipCounts.Sum()}  |  无 SBIN {result.ChipCounts.Sum() - result.ValidSBinCounts.Sum()}  |  无 HBIN {result.ChipCounts.Sum() - result.ValidHBinCounts.Sum()}  |  当前显示：{(counts ? "数量（颗）" : "占比（%）")}";
        TxtCopyStatus.Text = "";
    }

    private string BuildClipboardText()
    {
        var text = new StringBuilder();
        static string Cell(string value) => value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        void Row(IEnumerable<string> cells) => text.AppendLine(string.Join("\t", cells.Select(Cell)));
        bool counts = CountMode.IsChecked == true;
        Row(new[] { "Wafer Table", FirstMode.IsChecked == true ? "只保留首测" : "复测覆盖首测", counts ? "数量（颗）" : "占比（%）" });
        var result = FirstMode.IsChecked == true ? _first : _last;
        void Table(string title, DataGrid grid, string[] headers, Func<DisplayRow, string[]> labels, int[] denominators, string denominatorName)
        {
            text.AppendLine();
            Row(new[] { title });
            Row(headers.Concat(_wafers));
            foreach (var row in grid.Items.OfType<DisplayRow>())
                Row(labels(row).Concat(row.Values.Select(v => v?.ToString(counts ? "F0" : "F2", CultureInfo.CurrentCulture) ?? "–")));
            Row(new[] { denominatorName }.Concat(Enumerable.Repeat("", headers.Length - 1)).Concat(_wafers.Select(w =>
            {
                int index = result?.Wafers.IndexOf(w) ?? -1;
                return (index < 0 || index >= denominators.Length ? 0 : denominators[index]).ToString(CultureInfo.CurrentCulture);
            })));
        }
        Table("SBIN 各 Wafer", DgWafers, new[] { "SBIN", "分类" }, r => new[] { r.Bin, r.Category },
            result?.ValidSBinCounts ?? Array.Empty<int>(), "有效 SBIN 芯片数");
        Table("HBIN 各 Wafer", DgHBinWafers, new[] { "HBIN" }, r => new[] { r.Bin },
            result?.ValidHBinCounts ?? Array.Empty<int>(), "有效 HBIN 芯片数");
        Table("SBIN 分类各 Wafer", DgCategoryWafers, new[] { "分类" }, r => new[] { r.Category },
            result?.ValidSBinCounts ?? Array.Empty<int>(), "有效 SBIN 芯片数");
        return text.ToString();
    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BuildClipboardText()); TxtCopyStatus.Text = "已复制"; }
        catch (COMException) { TxtCopyStatus.Text = "剪贴板正忙，请重试"; }
    }
}
