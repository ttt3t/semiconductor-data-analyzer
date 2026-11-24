using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private IReadOnlyList<SampleRange> _sampleRanges = Array.Empty<SampleRange>();
    private IReadOnlyList<string> _sampleSBins = Array.Empty<string>(), _scopeSBins = Array.Empty<string>();
    private CsvParseResult? _scopeData;
    private CsvParseResult? _scopeSource;
    private IReadOnlyList<SampleRange> _scopeRanges = Array.Empty<SampleRange>();
    private Models.RetestMode _scopeMode;
    private string _scopeRetestDefinition = "";
    private SampleMatchMode _sampleMatch, _scopeMatch;
    private HashSet<string>? _sampleIncludedIds;
    private event Action? SampleFilterContextChanged;
    private CsvParseResult ActiveData => _scopeData ?? _data;
    private int SampleFilterCount => _sampleRanges.Count + _sampleSBins.Count;

    private void SampleFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _data.Records.Count == 0) return;
        var source = _data;
        var mode = _retestMode;
        var config = _config;
        var items = source.Items.Select(DatasetService.CloneItem).ToArray();
        var stores = _runLookup.Stores;
        Models.CsvRecord? CurrentRun() => !_closing && ReferenceEquals(_data, source) &&
            (uint)_runIndex < (uint)source.Records.Count ? source.Records[_runIndex] : null;
        var window = new SampleFilterWindow(items, _sampleRanges, mode, _sampleMatch,
            (rules, match, sBins) =>
            {
                EnsureWindowSource(source);
                if (_retestMode != mode || !ReferenceEquals(_config, config))
                    throw new InvalidOperationException("分析口径或配置已变化，请重新打开筛选窗口后应用。");
                return ApplySampleFiltersAsync(rules, match, sBins);
            }, _runLookup.SBins, _sampleSBins, CurrentRun,
            async (rules, sBins, token) =>
            {
                using var previewLease = new DatasetLease(stores);
                var run = CurrentRun();
                return await Task.Run(() =>
                {
                    using var operation = new AnalysisOperation(token);
                    return SampleFilterService.Preview(source, rules, mode, config, sBins, run);
                }, token);
            });
        var lease = new DatasetLease(stores);
        SampleFilterContextChanged += window.RefreshFocusedRows;
        window.Closed += (_, _) =>
        {
            SampleFilterContextChanged -= window.RefreshFocusedRows;
            lease.Dispose();
        };
        try { IndependentWindow.Show(window); }
        catch
        {
            SampleFilterContextChanged -= window.RefreshFocusedRows;
            lease.Dispose();
            throw;
        }
    }

    private async Task ApplySampleFiltersAsync(IReadOnlyList<SampleRange> rules, SampleMatchMode match, IReadOnlyList<string> sBins)
    {
        if (_isBusy || _closing) throw new InvalidOperationException("正在处理数据，请稍后应用筛选。");
        var previous = _sampleRanges;
        var previousMatch = _sampleMatch;
        var previousSBins = _sampleSBins;
        _sampleRanges = rules.ToArray(); _sampleMatch = match; _sampleSBins = sBins.ToArray();
        try
        {
            await RunBusyAsync(RefreshTestItemList);
            string ranges = _sampleRanges.Count == 0 ? "" : $"{_sampleRanges.Count} 个测项条件（{(match == SampleMatchMode.Union ? "并集" : "交集")}），";
            string bins = _sampleSBins.Count == 0 ? "" : $"{_sampleSBins.Count} 个 SBIN，";
            TxtStatus.Text = SampleFilterCount == 0 ? "已取消数据筛选，恢复全部样品。" :
                $"数据筛选：{ranges}{bins}保留 {ActiveData.Records.Count:N0} / {_data.Records.Count:N0} 条原始记录。";
        }
        catch { _sampleRanges = previous; _sampleMatch = previousMatch; _sampleSBins = previousSBins; throw; }
    }

    private void ScatterZoomAll_Checked(object sender, RoutedEventArgs e)
    { if (ScatterPlot != null) ScatterPlot.UseLimitRange = false; }
}
