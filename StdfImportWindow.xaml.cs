using System.IO;
using System.Windows;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class StdfImportWindow : Window
{
    public StdfImportMode SelectedMode => Mode93K.IsChecked == true
        ? StdfImportMode.Advantest93K : StdfImportMode.Standard;

    public StdfImportWindow(string path)
    {
        InitializeComponent();
        TxtFile.Text = Path.GetFileName(path);
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
