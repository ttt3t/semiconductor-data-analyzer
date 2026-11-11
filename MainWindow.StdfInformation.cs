using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private void BtnStdfInformation_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        IndependentWindow.Show(new StdfLotInformationWindow(_sources.Select(s => s.Path).ToArray()));
    }
}
