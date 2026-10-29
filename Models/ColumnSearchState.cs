using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Models;

/// <summary>Header-only state; no subscriptions or search objects on individual data rows.</summary>
public sealed class ColumnSearchState
{
    private string _text = "";
    private bool _enabled;
    private ColumnSearchRule _rule;
    public bool Numeric { get; }
    public ColumnSearchState(bool numeric) { Numeric = numeric; _rule = ColumnSearchRule.Compile("", numeric); }
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value ?? ""; _rule = ColumnSearchRule.Compile(_text, Numeric); }
    }
    public bool HasValueFilter { get; private set; }
    public bool CanSearch => !HasValueFilter;
    public bool? IsChecked => HasValueFilter ? null : _enabled;
    public bool IsSearchActive => _enabled && CanSearch && !string.IsNullOrWhiteSpace(_text);
    public string? Error => IsSearchActive ? _rule.Error : null;
    public void Enable(bool enabled) => _enabled = enabled && CanSearch;
    public void SetValueFilterActive(bool active)
    {
        HasValueFilter = active;
        if (active) _enabled = false; // Removing a checklist never silently reactivates an old query.
    }
    public void FillFromCell(string text) { Enable(false); Text = text; }
    public bool Matches(string text, double? number) => !IsSearchActive || _rule.Matches(text, number);
}
