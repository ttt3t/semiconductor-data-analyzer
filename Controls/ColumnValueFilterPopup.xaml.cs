using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Created only while a header's checklist is open; no per-row subscriptions on the main table.</summary>
public partial class ColumnValueFilterPopup : Popup
{
    public sealed class Choice : INotifyPropertyChanged
    {
        public string Value { get; }
        public string Label => Value.Length == 0 ? "(空白)" : Value;
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public Choice(string value, bool isChecked) { Value = value; _isChecked = isChecked; }
    }

    private readonly List<Choice> _choices;
    private readonly ListCollectionView _view;
    private Action<HashSet<string>?>? _apply;
    private readonly DispatcherTimer _searchTimer;

    public ColumnValueFilterPopup(string title, IEnumerable<string> values, ISet<string>? selected,
        Action<HashSet<string>?> apply)
    {
        _apply = apply;
        _choices = values.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Select(s => new Choice(s, selected == null || selected.Contains(s))).ToList();
        _view = new ListCollectionView(_choices);
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshSearch(); };
        InitializeComponent();
        TxtTitle.Text = "筛选：" + title;
        ValuesGrid.ItemsSource = _view;
        RefreshSearch();
        Opened += (_, _) => SearchBox.Focus();
        Closed += (_, _) =>
        {
            _searchTimer.Stop();
            ValuesGrid.ItemsSource = null;
            _view.Filter = null;
            _choices.Clear();
            _apply = null;
            PlacementTarget = null;
        };
        Child.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { IsOpen = false; e.Handled = true; } };
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RefreshSearch()
    {
        string search = SearchBox.Text.Trim();
        _view.Filter = search.Length == 0 ? null : o => o is Choice c && c.Label.Contains(search, StringComparison.OrdinalIgnoreCase);
        TxtCount.Text = $"显示 {_view.Count} / {_choices.Count} 个值；与表头关键字筛选同时生效";
    }

    private void SetVisible(bool selected)
    {
        _searchTimer.Stop();
        RefreshSearch();
        foreach (Choice choice in _view) choice.IsChecked = selected;
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e) => SetVisible(true);
    private void DeselectVisible_Click(object sender, RoutedEventArgs e) => SetVisible(false);
    private void Cancel_Click(object sender, RoutedEventArgs e) => IsOpen = false;
    private void Clear_Click(object sender, RoutedEventArgs e) { _apply?.Invoke(null); IsOpen = false; }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = _choices.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _apply?.Invoke(selected.Count == _choices.Count ? null : selected);
        IsOpen = false;
    }
}
