using System.IO;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private async Task OpenSelectedFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var source = _data;
        var selected = await ImportSourceSelection.SelectAsync(paths);
        if (selected == null) return;
        EnsureWindowSource(source);
        var mode = _windowPreferences.DefaultStdfMode;
        if (selected.Length == 1) { await OpenDataAsync(selected[0].Path, mode, selected[0] with { StdfMode = mode }); return; }
        var choices = new ImportDataWindow(selected, Array.Empty<string>(), initialImport: true);
        if (!await IndependentWindow.ShowAsync(choices)) return;
        EnsureWindowSource(source);
        var sources = choices.SelectedSources.Select((selected, index) => selected with
            { Options = index == 0 ? null : choices.Options, StdfMode = mode }).ToArray();
        await OpenBatchAsync(sources);
    }

    private async Task OpenBatchAsync(IReadOnlyList<CsvImportSource> sources)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据，请稍后重试。");
        if (sources.Count == 0) throw new InvalidDataException("请至少选择一个文件。");
        if (!_managedConfigLoaded && string.IsNullOrEmpty(_lastConfigPath) &&
            await Task.Run(() => sources.Any(DataFileService.NeedsLayout)))
            throw new InvalidOperationException("CSV / Excel 需要先在设置中加载行列配置；Excel 沿用 CSV 的行列定义。");
        await RunImportAsync(async () =>
        {
            var result = await ReadImportAsync(() => DatasetService.LoadSources(sources, _config));
            var prepared = await PrepareAnalysisAsync(result, _config);
            _columnValueFilters.Clear(); UpdateValueFilterHeaders();
            CommitAnalysis(result, prepared);
            _sources = sources.ToList(); _currentCsvPath = sources[0].Path;
            _currentDataName = $"整合数据（{sources.Count} 个文件 / {result.Wafers.Count} 个 wafer）";
            UpdateWindowTitle();
            foreach (var source in sources) _recentFiles.RememberCsv(source.Path);
            TxtStatus.Text = $"已按所选顺序打开 {sources.Count} 个文件，原始测试记录全部保留";
            ShowSerialNumberNotice(_data);
        });
    }

    private async Task AppendFilesAsync(IReadOnlyList<CsvImportSource> sources)
    {
        await RunImportAsync(async () =>
        {
            var merged = await ReadImportAsync(() => DatasetService.AppendSources(_data, sources, _config));
            var prepared = await PrepareAnalysisAsync(merged, _config);
            CommitAnalysis(merged, prepared);
            _sources.AddRange(sources);
            _currentDataName = $"整合数据（{_sources.Count} 个文件 / {_data.Wafers.Count} 个 wafer）";
            UpdateWindowTitle();
            foreach (var source in sources) _recentFiles.RememberCsv(source.Path);
            TxtStatus.Text = $"已导入 {sources.Count} 个文件，原始测试记录全部保留";
            ShowSerialNumberNotice(_data);
        });
    }
}
