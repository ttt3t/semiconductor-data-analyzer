using SemiconductorCsvAnalyzer.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        try
        {
            var parsed = MarkdownDocumentService.Render(MarkdownDocumentService.LoadUserGuide());
            DocumentViewer.Document = parsed.Document;
            ContentsList.ItemsSource = parsed.Headings;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            HelpStatus.Text = $"使用手册读取失败：{ex.Message}";
        }
        Closed += (_, _) =>
        {
            ContentsList.ItemsSource = null;
            DocumentViewer.Document = null;
        };
    }

    private void Contents_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ContentsList.SelectedItem is MarkdownHeading heading)
            heading.Block.BringIntoView();
    }

}
