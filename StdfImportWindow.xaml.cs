using System.IO;
using System.Windows;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer;

public partial class StdfImportWindow : Window
{
    public StdfImportMode SelectedMode => Mode93K.IsChecked == true
        ? StdfImportMode.Advantest93K : StdfImportMode.Standard;

    public StdfImportWindow(string path, int fileCount = 1)
    {
        InitializeComponent();
        TxtFile.Text = fileCount > 1 ? $"本次包含 {fileCount} 个 STDF 文件，所选模式统一应用于这些文件。\n首个文件：{Path.GetFileName(path)}" : Path.GetFileName(path);
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
