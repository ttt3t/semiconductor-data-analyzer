using System.Windows;

namespace SemiconductorCsvAnalyzer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.DiskStorage.ConfigureBudget(Services.UiPreferencesService.Current.Value.DataMemoryMiB);
        base.OnStartup(e);
        Window window = e.Args.Any(a => a.Equals("--analyzer", StringComparison.OrdinalIgnoreCase))
            ? new MainWindow() : new ProductManagementWindow();
        MainWindow = window; window.Show();
    }
}
