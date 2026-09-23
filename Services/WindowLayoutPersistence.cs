using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Restores once, saves after a short idle period and flushes on close.</summary>
public sealed class WindowLayoutPersistence
{
    private readonly Window _window;
    private readonly string _key;
    private readonly LayoutSettings _settings;
    private readonly WindowLayout? _saved;
    private readonly DispatcherTimer _timer;
    private readonly List<(string Key, DataGrid Grid)> _tables = new();
    private readonly Dictionary<DataGrid, List<SortLayout>> _sorts = new();
    private readonly List<(string Key, Func<double> Read, Action<double> Restore)> _splits = new();
    private readonly List<Action> _detach = new();
    private bool _restoring;
    private bool _loaded;
    private bool _closed;
    private bool _maximized;

    public WindowLayoutPersistence(Window window, string key, LayoutSettings? settings = null)
    {
        _window = window;
        _key = key;
        _settings = settings ?? LayoutSettings.Current;
        _saved = _settings.Get(key);
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => { _timer.Stop(); Save(); };

        if (_saved != null)
        {
            var area = SystemParameters.WorkArea;
            if (Positive(_saved.Width))
                window.Width = Math.Clamp(_saved.Width, Math.Max(200, window.MinWidth),
                    Math.Max(Math.Max(200, window.MinWidth), area.Width));
            if (Positive(_saved.Height))
                window.Height = Math.Clamp(_saved.Height, Math.Max(150, window.MinHeight),
                    Math.Max(Math.Max(150, window.MinHeight), area.Height));
            if (_saved.Maximized) window.WindowState = WindowState.Maximized;
        }
        _maximized = window.WindowState == WindowState.Maximized;
        window.Loaded += OnLoaded;
        window.SizeChanged += OnSizeChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    public void TrackTable(string key, DataGrid grid)
    {
        _tables.Add((key, grid));
        var saved = _saved?.Tables?.GetValueOrDefault(key);
        _sorts[grid] = saved?.Sorts?.Where(s => s != null &&
            Enum.IsDefined(s.Direction) && !string.IsNullOrEmpty(s.Property) &&
            grid.Columns.Any(c => c.CanUserSort && SortPath(c) == s.Property))
            .GroupBy(s => s.Property).Select(g => g.First()).ToList() ?? new();
        if (saved?.Columns != null)
            RestoreColumns(grid, saved.Columns);

        foreach (var column in grid.Columns)
        {
            Watch(column, DataGridColumn.WidthProperty, ScheduleSave);
            Watch(column, DataGridColumn.DisplayIndexProperty, ScheduleSave);
            Watch(column, DataGridColumn.VisibilityProperty, ScheduleSave);
        }
        // DataGrid clears sort descriptions when a new CSV/list replaces ItemsSource.
        Watch(grid, ItemsControl.ItemsSourceProperty, () =>
        {
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_closed) RestoreSorts(grid);
            }));
        });
        DataGridSortingEventHandler sorting = (_, _) =>
        {
            grid.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (_closed || _restoring) return;
                _sorts[grid] = grid.Items.SortDescriptions.Select(s => new SortLayout
                { Property = s.PropertyName, Direction = s.Direction }).ToList();
                ScheduleSave();
            }));
        };
        grid.Sorting += sorting;
        _detach.Add(() => grid.Sorting -= sorting);
    }

    public void TrackColumns(string key, ColumnDefinition first, ColumnDefinition second)
    {
        _splits.Add((key,
            () => Ratio(first.ActualWidth, second.ActualWidth),
            ratio => { first.Width = new GridLength(ratio, GridUnitType.Star);
                       second.Width = new GridLength(1 - ratio, GridUnitType.Star); }));
        Watch(first, ColumnDefinition.WidthProperty, ScheduleSave);
        Watch(second, ColumnDefinition.WidthProperty, ScheduleSave);
    }

    public void TrackRows(string key, RowDefinition first, RowDefinition second)
    {
        _splits.Add((key,
            () => Ratio(first.ActualHeight, second.ActualHeight),
            ratio => { first.Height = new GridLength(ratio, GridUnitType.Star);
                       second.Height = new GridLength(1 - ratio, GridUnitType.Star); }));
        Watch(first, RowDefinition.HeightProperty, ScheduleSave);
        Watch(second, RowDefinition.HeightProperty, ScheduleSave);
    }

    private static string ColumnKey(DataGridColumn column) =>
        column is DataGridBoundColumn { Binding: Binding binding } &&
        !string.IsNullOrEmpty(binding.Path?.Path)
            ? binding.Path.Path : column.SortMemberPath;

    private static string SortPath(DataGridColumn column) =>
        string.IsNullOrEmpty(column.SortMemberPath) ? ColumnKey(column) : column.SortMemberPath;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static double Ratio(double first, double second) =>
        Positive(first + second) ? first / (first + second) : 0.5;

    private static void RestoreColumns(DataGrid grid, IEnumerable<ColumnLayout> saved)
    {
        // Stable binding keys survive renamed headers and added/removed columns.
        var byKey = saved.Where(c => c != null && !string.IsNullOrEmpty(c.Key))
            .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var ordered = grid.Columns.OrderBy(c =>
            byKey.TryGetValue(ColumnKey(c), out var state) && state.DisplayIndex >= 0
                ? state.DisplayIndex : int.MaxValue).ToList();
        for (int index = 0; index < ordered.Count; index++)
        {
            var column = ordered[index];
            column.DisplayIndex = index;
            if (!byKey.TryGetValue(ColumnKey(column), out var state)) continue;
            if (Enum.IsDefined(state.WidthUnit) && double.IsFinite(state.Width) && state.Width >= 0)
            {
                double width = state.WidthUnit == DataGridLengthUnitType.Pixel
                    ? Math.Clamp(state.Width, column.MinWidth, Math.Min(column.MaxWidth, 10000))
                    : state.Width;
                column.Width = new DataGridLength(width, state.WidthUnit);
            }
            if (Enum.IsDefined(state.Visibility)) column.Visibility = state.Visibility;
        }
        // Never restore an unusable, completely hidden table from a damaged settings file.
        if (grid.Columns.Count > 0 && grid.Columns.All(c => c.Visibility != Visibility.Visible))
            foreach (var column in grid.Columns) column.Visibility = Visibility.Visible;
    }

    private void RestoreSorts(DataGrid grid)
    {
        if (!grid.Items.CanSort) return;
        _restoring = true;
        try
        {
            using (grid.Items.DeferRefresh())
            {
                grid.Items.SortDescriptions.Clear();
                foreach (var column in grid.Columns) column.SortDirection = null;
                foreach (var sort in _sorts[grid])
                {
                    grid.Items.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
                    foreach (var column in grid.Columns.Where(c => SortPath(c) == sort.Property))
                        column.SortDirection = sort.Direction;
                }
            }
        }
        finally { _restoring = false; }
    }

    private void Watch(DependencyObject source, DependencyProperty property, Action action)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, source.GetType());
        EventHandler handler = (_, _) => action();
        descriptor.AddValueChanged(source, handler);
        _detach.Add(() => descriptor.RemoveValueChanged(source, handler));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _restoring = true;
        foreach (var split in _splits)
            if (_saved?.SplitRatios?.TryGetValue(split.Key, out double ratio) == true &&
                double.IsFinite(ratio) && ratio > 0 && ratio < 1)
                split.Restore(Math.Clamp(ratio, 0.05, 0.95));
        _restoring = false;
        foreach (var table in _tables) RestoreSorts(table.Grid);
        _loaded = true;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleSave();
    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimizing must not replace the previous normal/maximized state.
        if (_window.WindowState != WindowState.Minimized)
            _maximized = _window.WindowState == WindowState.Maximized;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (!_loaded || _restoring || _closed) return;
        _timer.Stop();
        _timer.Start();
    }

    private void Save()
    {
        if (!_loaded) return;
        var bounds = _window.RestoreBounds;
        var layout = new WindowLayout
        {
            Width = Positive(bounds.Width) ? bounds.Width : _window.Width,
            Height = Positive(bounds.Height) ? bounds.Height : _window.Height,
            Maximized = _maximized
        };
        foreach (var table in _tables)
        {
            layout.Tables[table.Key] = new TableLayout
            {
                Columns = table.Grid.Columns.Select(c => new ColumnLayout
                {
                    Key = ColumnKey(c), DisplayIndex = c.DisplayIndex,
                    Width = c.Width.Value, WidthUnit = c.Width.UnitType, Visibility = c.Visibility
                }).ToList(),
                Sorts = _sorts[table.Grid].ToList()
            };
        }
        foreach (var split in _splits) layout.SplitRatios[split.Key] = split.Read();
        _settings.Save(_key, layout);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Save();
        _closed = true;
        foreach (var detach in _detach) detach();
        _detach.Clear();
        _window.Loaded -= OnLoaded;
        _window.SizeChanged -= OnSizeChanged;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
    }
}
