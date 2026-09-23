using System.ComponentModel;

namespace SemiconductorCsvAnalyzer.Models;

// Shared by the source item and its Site/retest views; no per-row subscriptions.
public sealed class TestItemFocus : INotifyPropertyChanged
{
    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new(nameof(IsFocused)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
