using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private IReadOnlyList<SampleRange> _sampleRanges = Array.Empty<SampleRange>();
    private CsvParseResult? _scopeData;
    private CsvParseResult? _scopeSource;
    private IReadOnlyList<SampleRange> _scopeRanges = Array.Empty<SampleRange>();
    private Models.RetestMode _scopeMode;
    private HashSet<string>? _sampleIncludedIds;
    private CsvParseResult ActiveData => _scopeData ?? _data;

    private async void SampleFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _data.Records.Count == 0) return;
        var dialog = new SampleFilterWindow(_data.Items, _sampleRanges, _retestMode) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var previous = _sampleRanges;
        _sampleRanges = dialog.Rules.ToArray();
        try
        {
            await RunBusyAsync(RefreshTestItemList);
            TxtStatus.Text = _sampleRanges.Count == 0 ? "已取消数据筛选，恢复全部样品。" :
                $"数据筛选：{_sampleRanges.Count} 个交集条件，保留 {ActiveData.Records.Count:N0} / {_data.Records.Count:N0} 条原始记录。";
        }
        catch (OperationCanceledException) { _sampleRanges = previous; }
        catch (Exception ex) { _sampleRanges = previous; MessageBox.Show(this, ex.Message, "数据筛选失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ScatterZoomAll_Checked(object sender, RoutedEventArgs e)
    { if (ScatterPlot != null) ScatterPlot.UseLimitRange = false; }
}
