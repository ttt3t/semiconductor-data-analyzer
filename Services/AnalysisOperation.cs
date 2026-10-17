using System.Collections.Concurrent;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Flows cancellation and ownership of newly created raw snapshots through background work.</summary>
public sealed class AnalysisOperation : IDisposable
{
    private static readonly AsyncLocal<AnalysisOperation?> Current = new();
    private readonly AnalysisOperation? _previous;
    private readonly ConcurrentBag<RawMeasurementStore> _stores = new();
    private bool _disposed;
    public static CancellationToken Token => Current.Value?._token ?? CancellationToken.None;
    private readonly CancellationToken _token;
    public AnalysisOperation(CancellationToken token = default)
    { _token = token; _previous = Current.Value; Current.Value = this; }
    public static void CheckCancellation() => Token.ThrowIfCancellationRequested();
    internal static bool Track(RawMeasurementStore store)
    {
        var operation = Current.Value;
        if (operation == null) return false;
        operation._stores.Add(store); return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Current.Value = _previous;
        while (_stores.TryTake(out var store)) store.DisposeIfUnleased();
    }
}

/// <summary>Pins operation-owned snapshots. Data created outside an operation retains its original GC-based lifetime.</summary>
public sealed class DatasetLease : IDisposable
{
    private RawMeasurementStore[]? _stores;
    public DatasetLease(IEnumerable<CsvRecord> records)
    {
        var acquired = new List<RawMeasurementStore>();
        try
        {
            foreach (var store in records.Select(r => r.Measurements.RawStore).OfType<RawMeasurementStore>().Distinct())
            { store.Acquire(); acquired.Add(store); }
            _stores = acquired.ToArray();
        }
        catch { foreach (var store in acquired) store.Release(); throw; }
    }
    public void Dispose()
    {
        var stores = Interlocked.Exchange(ref _stores, null);
        if (stores != null) foreach (var store in stores) store.Release();
    }
}
