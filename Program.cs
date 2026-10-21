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
        var app = new App(); app.InitializeComponent(); return app.Run();
    }
}
