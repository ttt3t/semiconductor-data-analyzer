using System.Text;
using System.Windows;
using System.Windows.Data;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ValuesWindow : Window
{
    private IReadOnlyList<ValueRow> _values;

    // Only this window's current test is read/cached. Table scrolling and sorting
    // never reread every source row, and sorting still uses the numeric value.
    private sealed class ValueRow
    {
        private readonly TestValue _sample;
        public ValueRow(TestValue sample) { _sample = sample; RawText = sample.RawText; }
        public string RawText { get; }
        public double Value => _sample.Value;
        public string SerialNumber => _sample.SerialNumber;
        public int? X => _sample.X;
        public int? Y => _sample.Y;
        public string Site => _sample.Site;
        public string SBin => _sample.SBin;
        public string HBin => _sample.HBin;
        public double SiteSort => _sample.SiteSort;
        public double SBinSort => _sample.SBinSort;
        public double HBinSort => _sample.HBinSort;
    }

    public ValuesWindow(string title, IReadOnlyList<TestValue> values)
    {
        InitializeComponent();
        _values = values.Select(value => new ValueRow(value)).ToArray();
        Title = title;
        TxtTitle.Text = title;
        DgValues.ItemsSource = new ListCollectionView((ValueRow[])_values);
        TxtCount.Text = $"共 {values.Count} 行";
        var layout = new WindowLayoutPersistence(this, "Values");
        layout.TrackTable("Values", DgValues);
        Closed += (_, _) => { DgValues.ItemsSource = null; _values = Array.Empty<ValueRow>(); };
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_values.Count == 0)
        {
            MessageBox.Show("没有可复制的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SN\tX\tY\tSite\tValue\tSBIN\tHBIN");
        foreach (var v in _values)
        {
            sb.Append(v.SerialNumber).Append('\t')
              .Append(v.X).Append('\t')
              .Append(v.Y).Append('\t')
              .Append(v.Site).Append('\t')
              .Append(v.RawText).Append('\t')
              .Append(v.SBin).Append('\t')
              .Append(v.HBin)
              .AppendLine();
        }

        Clipboard.SetText(sb.ToString());
        TxtCount.Text = $"共 {_values.Count} 行 | 已复制到剪贴板";
    }
}
