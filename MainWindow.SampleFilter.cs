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
    private string _scopeRetestDefinition = "";
    private SampleMatchMode _sampleMatch, _scopeMatch;
    private SampleFilterWindow? _sampleFilterWindow;
    private HashSet<string>? _sampleIncludedIds;
    private CsvParseResult ActiveData => _scopeData ?? _data;

    private void SampleFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _data.Records.Count == 0) return;
        if (_sampleFilterWindow != null) { _sampleFilterWindow.Activate(); return; }
        _sampleFilterWindow = new SampleFilterWindow(_data.Items, _sampleRanges, _retestMode,
            _sampleMatch, ApplySampleFiltersAsync) { Owner = this };
        _sampleFilterWindow.Closed += (_, _) => _sampleFilterWindow = null;
        _sampleFilterWindow.Show();
    }

    private async Task ApplySampleFiltersAsync(IReadOnlyList<SampleRange> rules, SampleMatchMode match)
    {
        if (_isBusy || _closing) throw new InvalidOperationException("正在处理数据，请稍后应用筛选。");
        var previous = _sampleRanges;
        var previousMatch = _sampleMatch;
        _sampleRanges = rules.ToArray(); _sampleMatch = match;
        try
        {
            await RunBusyAsync(RefreshTestItemList);
            TxtStatus.Text = _sampleRanges.Count == 0 ? "已取消数据筛选，恢复全部样品。" :
                $"数据筛选：{_sampleRanges.Count} 个{(match == SampleMatchMode.Union ? "并集" : "交集")}条件，保留 {ActiveData.Records.Count:N0} / {_data.Records.Count:N0} 条原始记录。";
        }
        catch { _sampleRanges = previous; _sampleMatch = previousMatch; throw; }
    }

    private void ScatterZoomAll_Checked(object sender, RoutedEventArgs e)
    { if (ScatterPlot != null) ScatterPlot.UseLimitRange = false; }
}
