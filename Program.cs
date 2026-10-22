using System.Windows.Threading;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--storage-worker")
        {
            try { return ExportWorker.Execute(args[1]); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }
        try
        {
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                System.Windows.MessageBox.Show(CrashReport.Describe(e.Exception), "程序错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                app.Shutdown(1);
            };
            app.InitializeComponent(); return app.Run();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(CrashReport.Describe(exception), "启动失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }
}
