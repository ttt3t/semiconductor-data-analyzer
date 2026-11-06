using System.Diagnostics;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ImportProgressSnapshot(double Fraction, string Stage);

/// <summary>Operation-local, throttled progress. Workers publish snapshots; the UI polls them.</summary>
public sealed class ImportProgress : IDisposable
{
    private static readonly AsyncLocal<Range?> Current = new();
    private readonly Range? _previous;
    private readonly object _gate = new();
    private ImportProgressSnapshot _latest = new(0, "准备导入…");
    private long _lastUpdate;
    private bool _disposed;
    public ImportProgressSnapshot Latest => Volatile.Read(ref _latest);
    public static bool IsActive => Current.Value != null;

    public ImportProgress()
    {
        _previous = Current.Value;
        Current.Value = new Range(this, 0, 1, "");
    }

    public static IDisposable Stage(double start, double end, string label)
    {
        if (Current.Value is not { } parent) return EmptyScope.Instance;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > 1 || start > end)
            throw new ArgumentOutOfRangeException(nameof(start));
        var range = new Range(parent.Owner, parent.Start + parent.Width * start,
            parent.Width * (end - start), string.IsNullOrEmpty(parent.Label) ? label
                : string.IsNullOrEmpty(label) ? parent.Label : parent.Label + " · " + label);
        Current.Value = range;
        range.Owner.Publish(range.Start, range.Label, true);
        return new RangeScope(parent);
    }

    public static void Report(double fraction, bool force = false)
    {
        if (Current.Value is not { } range || !double.IsFinite(fraction)) return;
        range.Owner.Publish(range.Start + range.Width * Math.Clamp(fraction, 0, 1), range.Label, force);
    }

    private void Publish(double fraction, string stage, bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now - Volatile.Read(ref _lastUpdate) < Stopwatch.Frequency / 10) return;
        lock (_gate)
        {
            if (_disposed || !force && now - _lastUpdate < Stopwatch.Frequency / 10) return;
            _lastUpdate = now;
            // Reading a buffered/compressed stream can reach EOF before analysis finishes.
            // Only the caller's successful commit may publish 100%.
            Volatile.Write(ref _latest, new(Math.Max(_latest.Fraction, Math.Min(fraction, .999)), stage));
        }
    }

    public void Complete()
    {
        lock (_gate)
            if (!_disposed) Volatile.Write(ref _latest, new(1, "导入完成"));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Current.Value = _previous;
    }

    private sealed record Range(ImportProgress Owner, double Start, double Width, string Label);
    private sealed class RangeScope(Range previous) : IDisposable
    {
        private Range? _previous = previous;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _previous, null) is { } parent) Current.Value = parent;
        }
    }
    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
