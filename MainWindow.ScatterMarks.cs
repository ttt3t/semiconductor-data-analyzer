using System.Windows;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private void ClearScatterMarks_Click(object sender, RoutedEventArgs e) => ScatterPlot.ClearChipMarks();

    private void ScatterChipMarks_Changed(object? sender, EventArgs e)
    {
        if (BtnClearScatterMarks == null || ScatterPlot == null) return;
        int count = ScatterPlot.ChipMarkCount;
        BtnClearScatterMarks.IsEnabled = count > 0;
        BtnClearScatterMarks.Content = count > 0 ? $"清空标记（{count}）" : "清空标记";
    }
}
