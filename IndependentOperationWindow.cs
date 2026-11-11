using System.Windows;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

/// <summary>Owns an export's cancellation and progress independently of the analyzer that started it.</summary>
public sealed class IndependentOperationWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _action;
    private readonly CancellationTokenSource _cancel = new();
    private bool _closed, _completed;

    private IndependentOperationWindow(string title)
    {
        Title = title; Width = 560; Height = 240;
        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        _action = new Button { Content = "取消", Padding = new Thickness(18, 5, 18, 5), HorizontalAlignment = HorizontalAlignment.Right };
        _status = new TextBlock { Text = "正在导出…", TextWrapping = TextWrapping.Wrap };
        _action.Click += (_, _) =>
        {
            if (_completed) Close();
            else { _cancel.Cancel(); _action.IsEnabled = false; _status.Text = "正在取消…"; }
        };
        DockPanel.SetDock(_action, Dock.Bottom); root.Children.Add(_action);
        _progress = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 12, 0, 16) };
        DockPanel.SetDock(_progress, Dock.Bottom); root.Children.Add(_progress);
        root.Children.Add(new ScrollViewer { Content = _status, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Closed += (_, _) => { _closed = true; if (!_completed) _cancel.Cancel(); };
    }

    public static async Task RunAsync(string title, Func<Task<string>> operation)
    {
        var window = new IndependentOperationWindow(title);
        IndependentWindow.Show(window);
        using var scope = new AnalysisOperation(window._cancel.Token);
        string message;
        try { message = await operation(); }
        catch (OperationCanceledException) { message = "已取消导出。"; }
        catch (Exception ex) { message = "导出失败：" + ReportExportFailure.Describe(ex); }
        finally { window._completed = true; window._cancel.Dispose(); }
        if (window._closed) return;
        window._status.Text = message;
        window._progress.IsIndeterminate = false;
        window._action.Content = "关闭";
        window._action.IsEnabled = true;
    }
}
