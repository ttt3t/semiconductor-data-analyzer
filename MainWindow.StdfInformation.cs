using System.Windows;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private StdfLotInformationWindow? _stdfInformationWindow;
    private void BtnStdfInformation_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_stdfInformationWindow == null)
        {
            var window = new StdfLotInformationWindow(_sources.Select(s => s.Path)) { Owner = this };
            _stdfInformationWindow = window;
            window.Closed += (_, _) => _stdfInformationWindow = null;
            window.Show();
        }
        else
        {
            if (_stdfInformationWindow.WindowState == WindowState.Minimized) _stdfInformationWindow.WindowState = WindowState.Normal;
            _stdfInformationWindow.Activate();
        }
    }
    private void RefreshStdfInformationSources() => _stdfInformationWindow?.UpdateSources(_sources.Select(s => s.Path));
}
