using System.ComponentModel;
using System.Windows;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ManagementReportOptionsWindow : Window
{
    public sealed class ScopeNode : INotifyPropertyChanged
    {
        public string Label { get; init; } = "";
        public ManagementScope? Scope { get; init; }
        public ScopeNode? Parent { get; set; }
        public List<ScopeNode> Children { get; } = new();
        private bool? _selected;
        private bool _visible = true;
        public bool Visible { get => _visible; set { _visible = value; Changed(nameof(Visible)); } }
        public bool? Selected
        {
            get => _selected;
            set { if (value == null) return; Apply(value.Value); Parent?.Refresh(); SelectionChanged?.Invoke(); }
        }
        public Action? SelectionChanged { get; set; }
        internal void Apply(bool selected) { _selected = selected; foreach (var child in Children) child.Apply(selected); Changed(nameof(Selected)); }
        internal void Refresh() { _selected = Children.All(c => c.Selected == true) ? true : Children.All(c => c.Selected == false) ? false : null; Changed(nameof(Selected)); Parent?.Refresh(); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }
    private readonly List<ScopeNode> _lots = new();
    private IEnumerable<ScopeNode> Leaves => _lots.SelectMany(l => l.Children);
    public ManagementReportOptions Options { get; private set; } = new(new());
    public ManagementReportOptionsWindow(ManagedProduct product, string filter = "", double target = 85)
    {
        InitializeComponent(); Target.Text = target.ToString("F1");
        foreach (var group in ManagementAnalytics.Scopes(product).GroupBy(s => s.Lot))
        {
            var lot = new ScopeNode { Label = group.Key + $"  ·  {group.Count()} 项", SelectionChanged = UpdateSummary };
            foreach (var scope in group)
            {
                var node = new ScopeNode { Label = scope.Kind == "FT" ? "FT · 批次" : "CP · " + scope.Wafer, Scope = scope, Parent = lot, SelectionChanged = UpdateSummary };
                node.Apply(ManagementAnalytics.Matches(scope.Lot, scope.Wafer, filter)); lot.Children.Add(node);
            }
            lot.Refresh(); _lots.Add(lot);
        }
        ScopeTree.ItemsSource = _lots; UpdateSummary();
    }
    private void UpdateSummary() => Summary.Text = $"已选择 {Leaves.Count(n => n.Selected == true)} 项，覆盖 {_lots.Count(l => l.Children.Any(n => n.Selected == true))} 个批次。";
    private void All_Click(object sender, RoutedEventArgs e) { foreach (var lot in _lots) lot.Apply(true); UpdateSummary(); }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var lot in _lots) lot.Apply(false); UpdateSummary(); }
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        foreach (var lot in _lots)
        {
            foreach (var node in lot.Children) node.Visible = lot.Label.Contains(Search.Text, StringComparison.OrdinalIgnoreCase) || node.Label.Contains(Search.Text, StringComparison.OrdinalIgnoreCase);
            lot.Visible = lot.Children.Any(n => n.Visible);
        }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var selected = Leaves.Where(n => n.Selected == true).Select(n => n.Scope!.Value).ToHashSet();
        if (selected.Count == 0) { Error.Text = "请至少勾选一个批次或晶圆。"; return; }
        if (!double.TryParse(Target.Text, out double target) || !double.IsFinite(target) || target is < 0 or > 100) { Error.Text = "良率参考线应为 0–100。"; return; }
        Options = new(selected, target); DialogResult = true;
    }
}
