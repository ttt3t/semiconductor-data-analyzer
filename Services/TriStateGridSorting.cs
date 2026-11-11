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
        UpdateIndicators(grid);
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
            UpdateIndicators(grid);
        }
        // Persist synchronously: closing or replacing ItemsSource immediately must not resurrect the old sort.
        grid.RaiseEvent(new RoutedEventArgs(SortChangedEvent, grid));
    }

    public static void UpdateIndicators(DataGrid grid)
    {
        foreach (var column in grid.Columns)
            column.SortDirection = grid.Items.SortDescriptions.Cast<SortDescription?>()
                .FirstOrDefault(sort => sort?.PropertyName == SortPath(column))?.Direction;
    }
}
