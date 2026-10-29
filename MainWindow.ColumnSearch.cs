using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow
{
    private static IEnumerable<T> HeaderControls<T>(Panel panel) where T : FrameworkElement
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is T found) yield return found;
            if (child is Panel nested)
                foreach (var descendant in HeaderControls<T>(nested)) yield return descendant;
        }
    }

    private static TextBox? FilterBox(DataGridColumn column) => column.Header is Panel panel
        ? HeaderControls<TextBox>(panel).FirstOrDefault(t => t.Tag is string) : null;
    private static string? FilterKey(DataGridColumn column) => FilterBox(column)?.Tag as string;

    private ColumnSearchState ColumnSearch(string key)
    {
        if (!_columnFilters.TryGetValue(key, out var state))
            _columnFilters[key] = state = new(key is not ("DisplayName" or "Unit" or "Wafer"));
        return state;
    }

    private void InitializeColumnSearch()
    {
        foreach (var column in DgTestItems.Columns)
            if (FilterKey(column) is string key) ColumnSearch(key);
        UpdateValueFilterHeaders();
    }

    private void ColumnFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } box) return;
        var state = ColumnSearch(key);
        state.Text = box.Text;
        UpdateValueFilterHeaders();
        // Clearing an enabled query also removes its previous restriction.
        if (!_imeComposing && state.IsChecked == true) ScheduleFilter();
    }

    private void ColumnSearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } toggle) return;
        ColumnSearch(key).Enable(toggle.IsChecked == true);
        UpdateValueFilterHeaders();
        ScheduleFilter();
        e.Handled = true;
    }

    private void UpdateValueFilterHeaders()
    {
        if (DgTestItems == null) return; // TextBox events may arrive while BAML creates the headers.
        foreach (var column in DgTestItems.Columns)
        {
            if (FilterBox(column) is not { Tag: string key } box || column.Header is not Panel panel) continue;
            var state = ColumnSearch(key);
            state.SetValueFilterActive(_columnValueFilters.ContainsKey(key));
            box.IsEnabled = state.CanSearch;
            box.ToolTip = state.HasValueFilter ? "此列由右键筛选器控制；清除此列筛选后可勾选搜索。" :
                state.Error is string error ? error + "\n" + ColumnSearchRule.Help : ColumnSearchRule.Help;
            if (state.Error != null) box.BorderBrush = Brushes.Firebrick;
            else box.ClearValue(Control.BorderBrushProperty);
            if (HeaderControls<CheckBox>(panel).FirstOrDefault(c => Equals(c.Tag, key)) is { } toggle)
            {
                toggle.IsChecked = state.IsChecked;
                toggle.IsEnabled = state.CanSearch;
                toggle.ToolTip = state.HasValueFilter ? "右键筛选已启用" : "勾选后此列搜索才生效";
            }
            panel.Background = state.HasValueFilter || state.IsSearchActive ? Brushes.LightBlue : Brushes.Transparent;
        }
    }

    private void SetColumnSearchVisibility(bool visible)
    {
        bool changed = false;
        foreach (var column in DgTestItems.Columns)
        {
            if (FilterBox(column) is not { Tag: string key, Parent: DockPanel row } box) continue;
            if (!visible)
            {
                var state = ColumnSearch(key);
                changed |= state.IsSearchActive;
                state.Enable(false); box.Clear();
            }
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateValueFilterHeaders();
        if (changed) ScheduleFilter();
    }

    private void FillColumnSearchFromCell(DataGridColumn column, string text)
    {
        if (FilterBox(column) is not { Tag: string key } box) return;
        var state = ColumnSearch(key);
        bool wasActive = state.IsSearchActive;
        state.FillFromCell(text);
        box.Text = text;
        UpdateValueFilterHeaders();
        if (wasActive) ScheduleFilter();
    }

    private void Focus_Click(object sender, RoutedEventArgs e) => RefreshFocusColumnFilter();
    private void RefreshFocusColumnFilter()
    {
        if (_columnValueFilters.ContainsKey("Focus") || ColumnSearch("Focus").IsSearchActive) ScheduleFilter();
    }

    private static double? ColumnNumber(TestItemSummary s, string key) => key switch
    {
        "Focus" => s.Data.Focus.IsFocused ? 1 : 0,
        "TestNumber" => s.TestNumberValue,
        "Site" => ColumnSearchRule.ParseNumber(s.Site),
        "SBin" => ColumnSearchRule.ParseNumber(s.SBin),
        "HBin" => ColumnSearchRule.ParseNumber(s.HBin),
        "Lsl" => s.Data.DisplayLowLimit,
        "Usl" => s.Data.DisplayHighLimit,
        "Count" => s.Count,
        "Fails" => s.Fails,
        "Eorr" => s.Eorr,
        "SiteSigma" => s.SiteSigma,
        "Max" => s.Max,
        "Min" => s.Min,
        "RunValue" => s.RunValue,
        "Mean" => s.Count > 0 ? s.Mean : null,
        "Median" => s.Count > 0 ? s.Median : null,
        "Sigma" => s.Count > 0 ? s.Sigma : null,
        "RobustMean" => s.Count > 0 ? s.RobustMean : null,
        "RobustSigma" => s.Count > 0 ? s.RobustSigma : null,
        "Cpk" => s.Cpk,
        "InSpec" => s.Count > 0 ? s.InSpecPercent : null,
        _ => null
    };
}
