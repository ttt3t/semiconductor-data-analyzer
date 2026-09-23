using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class ExportDataWindow : Window
{
    public sealed class WaferChoice : INotifyPropertyChanged
    {
        public string Name { get; }
        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public WaferChoice(string name) => Name = name;
    }
    private readonly List<WaferChoice> _wafers;
    public string[] SelectedWafers { get; private set; } = Array.Empty<string>();
    public double? OutlierSigma { get; private set; }
    public int HistogramMode => ReportHistogramMode.SelectedIndex;
    public event Action<int>? HistogramModeChanged;
    public bool CombineSites => ReportCombineSites.IsChecked == true;
    public bool ShowBinNumbers => ReportBinNumbers.IsChecked == true;
    public bool ShowSiteBorders => ReportSiteBorders.IsChecked == true;
    public RetestMode Mode => KeepFirst.IsChecked == true ? RetestMode.First : KeepLast.IsChecked == true ? RetestMode.Last : RetestMode.All;
    public ExportDataWindow(IEnumerable<string> wafers, double defaultOutlierSigma = 6,
        bool report = false, int histogramMode = 0, bool combineSites = false, int focusedCount = 0)
    {
        InitializeComponent();
        TxtOutlierSigma.Text = (double.IsFinite(defaultOutlierSigma) && defaultOutlierSigma > 0
            ? defaultOutlierSigma : 6).ToString("G", CultureInfo.InvariantCulture);
        if (report)
        {
            Title = "导出 XLSX 报告";
            Width = 870; MinWidth = 810;
            Height = 710; MinHeight = 670;
            DockPanel.SetDock(ExportOptions, Dock.Right);
            ExportOptions.Width = 410; ExportOptions.Margin = new Thickness(14, 0, 0, 0);
            ReportSettings.Visibility = Visibility.Visible;
            ReportHistogramMode.SelectedIndex = Math.Clamp(histogramMode, 0, 2);
            ReportCombineSites.IsChecked = combineSites;
            ReportFocusInfo.Text = $"已关注 {focusedCount} 个测项，关注图表合并 Site。BIN 图与统计：仅首测时用首测，其余用末测。多个晶圆分别绘图。";
        }
        _wafers = wafers.Select(w => new WaferChoice(w)).ToList();
        WaferList.ItemsSource = new ListCollectionView(_wafers);
        Closed += (_, _) => { WaferList.ItemsSource = null; _wafers.Clear(); HistogramModeChanged = null; };
    }
    private void ReportHistogramMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReportHistogramMode?.SelectedIndex is >= 0 and <= 2)
            HistogramModeChanged?.Invoke(ReportHistogramMode.SelectedIndex);
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var w in _wafers) w.IsSelected = true; TxtError.Text = ""; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var w in _wafers) w.IsSelected = false; }
    private void ClearError(object sender, RoutedEventArgs e) { if (TxtError != null) TxtError.Text = ""; }
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        SelectedWafers = _wafers.Where(w => w.IsSelected).Select(w => w.Name).ToArray();
        if (SelectedWafers.Length == 0) { TxtError.Text = "请至少选择一个晶圆。"; return; }
        OutlierSigma = null;
        if (ChkRemoveOutliers.IsChecked == true)
        {
            if (!double.TryParse(TxtOutlierSigma.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sigma) ||
                !double.IsFinite(sigma) || sigma <= 0)
            { TxtError.Text = "离群阈值必须是大于 0 的有限数字，例如 6 或 3.5。"; return; }
            OutlierSigma = sigma;
        }
        DialogResult = true;
    }
}
