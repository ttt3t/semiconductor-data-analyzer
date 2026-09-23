using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Services;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class HelpWindow : Window
{
    private string _markdown = string.Empty;

    public HelpWindow()
    {
        InitializeComponent();
        try
        {
            _markdown = MarkdownDocumentService.LoadUserGuide();
            var parsed = MarkdownDocumentService.Render(_markdown);
            DocumentViewer.Document = parsed.Document;
            ContentsList.ItemsSource = parsed.Headings;
            RawMarkdown.Text = _markdown;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            HelpStatus.Text = $"使用手册读取失败：{ex.Message}";
        }
        Closed += (_, _) =>
        {
            ContentsList.ItemsSource = null;
            DocumentViewer.Document = null;
            RawMarkdown.Clear();
            _markdown = string.Empty;
        };
    }

    private void RawToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DocumentViewer == null || RawMarkdown == null) return;
        var raw = RawToggle.IsChecked == true;
        DocumentViewer.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        RawMarkdown.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        ContentsList.IsEnabled = !raw;
    }

    private void Contents_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ContentsList.SelectedItem is MarkdownHeading heading)
            heading.Block.BringIntoView();
    }

    private void SaveMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_markdown)) return;
        var dialog = new SaveFileDialog
        {
            Title = "保存使用手册", FileName = "CSV Analyzer V2.0 使用手册.md",
            Filter = "Markdown 文档 (*.md)|*.md", DefaultExt = ".md", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _markdown, new UTF8Encoding(false));
            HelpStatus.Text = $"已保存：{dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "无法保存使用手册", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
