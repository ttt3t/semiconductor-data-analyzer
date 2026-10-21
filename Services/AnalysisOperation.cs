using System.Collections.Concurrent;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Flows cancellation and ownership of newly created raw snapshots through background work.</summary>
public sealed class AnalysisOperation : IDisposable
{
    private static readonly AsyncLocal<AnalysisOperation?> Current = new();
    private readonly AnalysisOperation? _previous;
    private readonly ConcurrentBag<RawMeasurementStore> _stores = new();
    private readonly ConcurrentBag<DiskResource> _disk = new();
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
    internal static bool Track(DiskResource store)
    {
        var operation = Current.Value;
        if (operation == null || operation._disposed) return false;
        operation._disk.Add(store); return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Current.Value = _previous;
        while (_stores.TryTake(out var store)) store.DisposeIfUnleased();
        while (_disk.TryTake(out var resource)) resource.DisposeIfUnleased();
    }
}

/// <summary>Pins operation-owned snapshots. Data created outside an operation retains its original GC-based lifetime.</summary>
public sealed class DatasetLease : IDisposable
{
    private RawMeasurementStore[]? _stores;
    private DiskResource[]? _resources;
    public DatasetLease(RecordList records, params RecordList[] others)
    {
        var datasets = new[] { records }.Concat(others).Distinct().ToArray();
        _resources = datasets.SelectMany(d => d.Resources).Distinct().ToArray();
        var acquired = new List<DiskResource>(); var raw = new List<RawMeasurementStore>();
        try
        {
            foreach (var resource in _resources) { resource.Acquire(); acquired.Add(resource); }
            foreach (var store in datasets.SelectMany(d => d.RawStores.Count > 0 ? d.RawStores : d.Select(r => r.Measurements.RawStore).OfType<RawMeasurementStore>()).Distinct())
            { store.Acquire(); raw.Add(store); }
            _stores = raw.ToArray();
        }
        catch { foreach (var store in raw) store.Release(); foreach (var resource in acquired) resource.Release(); throw; }
    }
    public DatasetLease(IEnumerable<CsvRecord> records)
    {
        var acquired = new List<RawMeasurementStore>();
        var acquiredResources = new List<DiskResource>();
        try
        {
            if (records is RecordList diskRecords)
            {
                _resources = diskRecords.Resources.ToArray();
                foreach (var resource in _resources) { resource.Acquire(); acquiredResources.Add(resource); }
            }
            var snapshots = records is RecordList paged && paged.RawStores.Count > 0
                ? paged.RawStores : records.Select(r => r.Measurements.RawStore).OfType<RawMeasurementStore>().Distinct();
            foreach (var store in snapshots)
            { store.Acquire(); acquired.Add(store); }
            _stores = acquired.ToArray();
        }
        catch { foreach (var store in acquired) store.Release(); foreach (var resource in acquiredResources) resource.Release(); throw; }
    }
    public void Dispose()
    {
        var stores = Interlocked.Exchange(ref _stores, null);
        if (stores != null) foreach (var store in stores) store.Release();
        var resources = Interlocked.Exchange(ref _resources, null);
        if (resources != null) foreach (var resource in resources) resource.Release();
    }
}
