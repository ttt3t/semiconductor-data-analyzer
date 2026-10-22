using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Expression = System.Linq.Expressions.Expression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Header-only filter state and one collection view per management table; no row subscriptions.</summary>
public sealed class ManagementGridFilter : IDisposable
{
    public sealed class HeaderState : INotifyPropertyChanged
    {
        private string _text = "";
        internal Action? Changed;
        internal HashSet<string>? Selected;
        internal Binding Binding = null!;
        public required string Key { get; init; }
        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? ""; Notify(); Changed?.Invoke();
            }
        }
        public bool IsActive => _text.Trim().Length > 0 || Selected != null;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Notify() { PropertyChanged?.Invoke(this, new(nameof(Text))); PropertyChanged?.Invoke(this, new(nameof(IsActive))); }
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(HeaderState), typeof(ManagementGridFilter));
    public static HeaderState? GetState(DependencyObject target) => (HeaderState?)target.GetValue(StateProperty);
    public static void SetState(DependencyObject target, HeaderState? value) => target.SetValue(StateProperty, value);
    private readonly DataGrid _grid;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<DataGridColumn, HeaderState> _states = new();
    private readonly Dictionary<(Type, string), Func<object, object?>> _getters = new();
    private object[] _rows = Array.Empty<object>();
    private ListCollectionView? _view;
    private ColumnValueFilterPopup? _popup;
    private bool _disposed;

    public ManagementGridFilter(DataGrid grid, DataTemplate headerTemplate)
    {
        _grid = grid;
        _timer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += Tick;
        foreach (var column in grid.Columns.OfType<DataGridBoundColumn>())
        {
            if (column.Binding is not Binding { Path.Path: { Length: > 0 } path } binding) continue;
            var state = new HeaderState { Key = path, Binding = binding, Changed = Schedule };
            _states.Add(column, state); SetState(column, state); column.HeaderTemplate = headerTemplate;
            if (column.SortMemberPath.Length == 0) column.SortMemberPath = path;
        }
        grid.ColumnHeaderHeight = 55;
        grid.PreviewMouseRightButtonUp += HeaderRightClick;
        grid.PreviewMouseLeftButtonDown += HeaderMouseDown;
    }

    public void SetItems(IEnumerable rows)
    {
        if (_disposed) return;
        ClosePopup(); _timer.Stop();
        var sort = _view?.SortDescriptions.ToArray() ?? Array.Empty<SortDescription>();
        if (_view != null) _view.Filter = null;
        _rows = rows.Cast<object>().ToArray();
        _view = new ListCollectionView(_rows);
        using (_view.DeferRefresh())
        {
            _view.Filter = Matches;
            foreach (var entry in sort) _view.SortDescriptions.Add(entry);
        }
        _grid.ItemsSource = _view;
    }

    public string CellText(object row, HeaderState state)
    {
        var key = (row.GetType(), state.Key);
        if (!_getters.TryGetValue(key, out var getter))
        {
            var parameter = Expression.Parameter(typeof(object), "row");
            Expression property = Expression.Convert(parameter, key.Item1);
            foreach (string part in state.Key.Split('.')) property = Expression.Property(property, part);
            _getters[key] = getter = Expression.Lambda<Func<object, object?>>(Expression.Convert(property, typeof(object)), parameter).Compile();
        }
        object? value = getter(row);
        if (value == null) return state.Binding.TargetNullValue is string empty ? empty : "";
        string? format = state.Binding.StringFormat;
        if (!string.IsNullOrEmpty(format))
            return format.Contains('{') ? string.Format(CultureInfo.CurrentCulture, format, value) :
                value is IFormattable number ? number.ToString(format, CultureInfo.CurrentCulture) : value.ToString() ?? "";
        return value.ToString() ?? "";
    }

    private bool Matches(object row)
    {
        foreach (var state in _states.Values)
        {
            if (!state.IsActive) continue;
            string cell = CellText(row, state);
            if (state.Selected != null && !state.Selected.Contains(cell) ||
                state.Text.Trim() is { Length: > 0 } text && !cell.Contains(text, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    private void Schedule() { if (_disposed) return; _timer.Stop(); _timer.Start(); }
    private void Tick(object? sender, EventArgs e) { _timer.Stop(); _view?.Refresh(); }
    public void Clear()
    {
        ClosePopup();
        foreach (var state in _states.Values) { state.Selected = null; state.Text = ""; state.Notify(); }
        _timer.Stop(); _view?.Refresh();
    }
    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T) node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
    private void HeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<TextBox>(e.OriginalSource as DependencyObject) is { } box && !box.IsKeyboardFocusWithin)
        { box.Focus(); e.Handled = true; }
    }
    private void HeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column } header || !_states.TryGetValue(column, out var state)) return;
        e.Handled = true; OpenValueFilter(header, column, state);
    }
    private void OpenValueFilter(FrameworkElement target, DataGridColumn column, HeaderState state)
    {
        ClosePopup();
        var popup = new ColumnValueFilterPopup(column.Header?.ToString() ?? state.Key,
            _rows.Select(row => CellText(row, state)), state.Selected, selected =>
            { state.Selected = selected; state.Notify(); Schedule(); }) { PlacementTarget = target };
        popup.Closed += (_, _) => { if (ReferenceEquals(_popup, popup)) _popup = null; };
        _popup = popup; popup.IsOpen = true;
    }
    private void ClosePopup() { if (_popup != null) _popup.IsOpen = false; _popup = null; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick; ClosePopup();
        _grid.PreviewMouseRightButtonUp -= HeaderRightClick; _grid.PreviewMouseLeftButtonDown -= HeaderMouseDown;
        if (_view != null) _view.Filter = null;
        _grid.ItemsSource = null; _view = null; _rows = Array.Empty<object>(); _getters.Clear();
        foreach (var pair in _states) { pair.Value.Changed = null; SetState(pair.Key, null); }
        _states.Clear();
    }
}
