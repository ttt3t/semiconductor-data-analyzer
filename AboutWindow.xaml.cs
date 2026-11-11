using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void DetailedHelp_Click(object sender, RoutedEventArgs e)
    {
        IndependentWindow.Show(new HelpWindow());
    }
}
