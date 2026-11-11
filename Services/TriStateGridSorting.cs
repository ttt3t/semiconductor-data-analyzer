using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Ascending, descending, then the source's natural order; no copied rows or custom comparers.</summary>
public static class TriStateGridSorting
{
    private static bool _installed;
    private static readonly DependencyPropertyKey DescriptionPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "Description", typeof(string), typeof(TriStateGridSorting), new PropertyMetadata("默认排序"));
    public static readonly DependencyProperty DescriptionProperty = DescriptionPropertyKey.DependencyProperty;
    public static string GetDescription(DependencyObject value) => (string)value.GetValue(DescriptionProperty);

    public static readonly RoutedEvent SortChangedEvent = EventManager.RegisterRoutedEvent(
        "SortChanged", RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(TriStateGridSorting));

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) => { if (ReferenceEquals(sender, e.OriginalSource)) Attach((DataGrid)sender); }));
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler((sender, e) => { if (ReferenceEquals(sender, e.OriginalSource)) ((DataGrid)sender).Sorting -= OnSorting; }));
    }

    public static void Attach(DataGrid grid)
    {
        grid.Sorting -= OnSorting;
        grid.Sorting += OnSorting;
        UpdateDescription(grid);
    }

    private static string SortPath(DataGridColumn column) => column.SortMemberPath.Length > 0
        ? column.SortMemberPath : column is DataGridBoundColumn { Binding: Binding binding } ? binding.Path?.Path ?? "" : "";

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Handled || sender is not DataGrid grid || !grid.CanUserSortColumns || !e.Column.CanUserSort || !grid.Items.CanSort) return;
        string path = SortPath(e.Column);
        if (path.Length == 0) return;
        e.Handled = true;
        var direction = e.Column.SortDirection;
        using (grid.Items.DeferRefresh())
        {
            if (direction == ListSortDirection.Descending)
            {
                // The third click restores the whole table, including any Shift multi-column sort.
                if (CollectionViewSource.GetDefaultView(grid.ItemsSource ?? grid.Items) is ListCollectionView view && view.CustomSort != null)
                    view.CustomSort = null;
                grid.Items.SortDescriptions.Clear();
            }
            else
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) grid.Items.SortDescriptions.Clear();
                var next = direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                int index = -1;
                for (int i = 0; i < grid.Items.SortDescriptions.Count; i++)
                    if (grid.Items.SortDescriptions[i].PropertyName == path) { index = i; break; }
                var sort = new SortDescription(path, next);
                if (index >= 0) grid.Items.SortDescriptions[index] = sort;
                else grid.Items.SortDescriptions.Add(sort);
            }
            foreach (var column in grid.Columns)
                column.SortDirection = grid.Items.SortDescriptions.Cast<SortDescription?>()
                    .FirstOrDefault(s => s?.PropertyName == SortPath(column))?.Direction;
        }
        UpdateDescription(grid);
        // Persist synchronously: closing or replacing ItemsSource immediately must not resurrect the old sort.
        grid.RaiseEvent(new RoutedEventArgs(SortChangedEvent, grid));
    }

    public static void UpdateDescription(DataGrid grid)
    {
        var labels = grid.Items.SortDescriptions.Select(sort =>
        {
            var column = grid.Columns.FirstOrDefault(c => SortPath(c) == sort.PropertyName);
            string label = HeaderText(column?.Header);
            if (label.Length == 0) label = sort.PropertyName;
            return label.Replace('\r', ' ').Replace('\n', ' ') +
                (sort.Direction == ListSortDirection.Ascending ? " ↑ 升序" : " ↓ 降序");
        }).ToArray();
        grid.SetValue(DescriptionPropertyKey, labels.Length == 0 ? "默认排序" : string.Join("；", labels));
    }

    private static string HeaderText(object? header)
    {
        if (header is string text) return text;
        if (header is TextBlock block) return block.Text;
        if (header is DependencyObject element)
            foreach (object child in LogicalTreeHelper.GetChildren(element))
            {
                string label = HeaderText(child);
                if (label.Length > 0) return label;
            }
        return "";
    }
}
