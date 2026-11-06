using System.Windows;
using System.Windows.Threading;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private ImportProgress? _importProgress;

    private Task RunImportAsync(Func<Task> operation) => RunBusyAsync(async () =>
    {
        using var progress = new ImportProgress();
        _importProgress = progress;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => UpdateImportProgress();
        ImportProgressBar.Visibility = TxtImportPercent.Visibility = Visibility.Visible;
        UpdateImportProgress();
        timer.Start();
        try
        {
            await operation();
            progress.Complete();
            UpdateImportProgress();
        }
        finally
        {
            timer.Stop();
            _importProgress = null;
            ImportProgressBar.Visibility = TxtImportPercent.Visibility = Visibility.Collapsed;
            TxtBusyStage.Text = "处理中…";
            TxtBusyStage.ToolTip = null;
        }
    });

    private void UpdateImportProgress()
    {
        if (_importProgress == null) return;
        var snapshot = _importProgress.Latest;
        ImportProgressBar.Value = snapshot.Fraction * 100;
        TxtImportPercent.Text = $"{(int)(snapshot.Fraction * 100)}%";
        TxtBusyStage.Text = snapshot.Stage;
        TxtBusyStage.ToolTip = snapshot.Stage;
    }

    private static Task<CsvParseResult> ReadImportAsync(Func<CsvParseResult> read) => Task.Run(() =>
    {
        using var stage = ImportProgress.Stage(0, .85, "读取文件");
        var result = read();
        ImportProgress.Report(1, true);
        return result;
    });
}
