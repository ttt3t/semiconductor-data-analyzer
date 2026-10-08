using System.Collections.ObjectModel;
using System.Windows;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ImportDataWindow : Window
{
    public ImportOptions? Options { get; private set; }
    private sealed record ImportFile(string Path) { public string Name => System.IO.Path.GetFileName(Path); }
    private readonly ObservableCollection<ImportFile> _files;
    public string[] SelectedFiles => _files.Select(f => f.Path).ToArray();
    public ImportDataWindow(string fileName, IReadOnlyList<string> wafers) : this(new[] { fileName }, wafers) { }

    public ImportDataWindow(IReadOnlyList<string> files, IReadOnlyList<string> wafers)
    {
        InitializeComponent();
        _files = new ObservableCollection<ImportFile>(files.Select(path => new ImportFile(path)));
        TxtFile.Text = $"已选择 {files.Count} 个文件";
        FileList.ItemsSource = _files;
        FileList.SelectedIndex = files.Count > 0 ? 0 : -1;
        CmbTargetWafer.ItemsSource = wafers;
        CmbTargetWafer.SelectedIndex = wafers.Count > 0 ? 0 : -1;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveFile(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveFile(1);
    private void MoveFile(int offset)
    {
        int index = FileList.SelectedIndex, target = index + offset;
        if (index < 0 || target < 0 || target >= _files.Count) return;
        _files.Move(index, target);
        FileList.SelectedIndex = target;
        FileList.ScrollIntoView(_files[target]);
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0) { TxtError.Text = "请至少选择一个文件。"; return; }
        string target = CmbTargetWafer.SelectedItem as string ?? "";
        if (SameWafer.IsChecked == true && target.Length == 0)
        { TxtError.Text = "请选择要合并到的 wafer。"; return; }
        Options = new ImportOptions(SameWafer.IsChecked == true, target, MatchNumber.IsChecked == true, NewLimits.IsChecked == true);
        DialogResult = true;
    }
}
