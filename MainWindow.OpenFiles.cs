using System.IO;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private async Task OpenSelectedFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1) { await OpenCsvAsync(paths[0]); return; }
        var choices = new ImportDataWindow(paths, Array.Empty<string>(), initialImport: true) { Owner = this };
        if (choices.ShowDialog() != true) return;
        var ordered = choices.SelectedFiles;
        var stdf = ordered.Where(DataFileService.IsStdf).ToArray();
        var mode = stdf.Length == 0 ? StdfImportMode.Standard : SelectStdfMode(stdf[0], stdf.Length);
        if (!mode.HasValue) return;
        var sources = ordered.Select((path, index) => new CsvImportSource(path,
            index == 0 ? null : choices.Options, mode.Value)).ToArray();
        await OpenBatchAsync(sources);
    }

    private async Task OpenBatchAsync(IReadOnlyList<CsvImportSource> sources)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据，请稍后重试。");
        if (sources.Count == 0) throw new InvalidDataException("请至少选择一个文件。");
        if (!_managedConfigLoaded && string.IsNullOrEmpty(_lastConfigPath) &&
            sources.Any(s => !DataFileService.IsStdf(s.Path) && !IntegratedCsvService.IsIntegrated(s.Path)))
            throw new InvalidOperationException("普通 CSV 需要先在设置中加载配置，或点击 CSV 格式识别并应用结果。");
        await RunBusyAsync(async () =>
        {
            var result = await Task.Run(() => DatasetService.LoadSources(sources, _config));
            var prepared = await PrepareAnalysisAsync(result, _config);
            _columnValueFilters.Clear(); UpdateValueFilterHeaders();
            CommitAnalysis(result, prepared);
            _sources = sources.ToList(); _currentCsvPath = sources[0].Path;
            _currentDataName = $"整合数据（{sources.Count} 个文件 / {result.Wafers.Count} 个 wafer）";
            RefreshStdfInformationSources(); UpdateWindowTitle(); RefreshOpenMaps();
            foreach (var source in sources) _recentFiles.RememberCsv(source.Path);
            TxtStatus.Text = $"已按所选顺序打开 {sources.Count} 个文件，原始测试记录全部保留";
            ShowSerialNumberNotice(_data);
        });
    }
}
