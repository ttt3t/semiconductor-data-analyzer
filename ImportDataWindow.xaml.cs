using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ImportDataWindow : Window
{
    public ImportOptions? Options { get; private set; }
    public ImportDataWindow(string fileName, IReadOnlyList<string> wafers)
    {
        InitializeComponent();
        TxtFile.Text = fileName;
        CmbTargetWafer.ItemsSource = wafers;
        CmbTargetWafer.SelectedIndex = wafers.Count > 0 ? 0 : -1;
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        string target = CmbTargetWafer.SelectedItem as string ?? "";
        if (SameWafer.IsChecked == true && target.Length == 0)
        { TxtError.Text = "请选择要合并到的 wafer。"; return; }
        Options = new ImportOptions(SameWafer.IsChecked == true, target, MatchNumber.IsChecked == true, NewLimits.IsChecked == true);
        DialogResult = true;
    }
}
