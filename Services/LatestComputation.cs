namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Serial background execution; cancellation and revision checking also reject non-cooperative old work.</summary>
public sealed class LatestComputation : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1);
    private CancellationTokenSource? _current;
    private long _version;
    private bool _disposed;
    public void Invalidate()
    {
        lock (_sync) { _version++; _current?.Cancel(); }
    }
    public async Task RunAsync<T>(Func<CancellationToken, T> compute, Action<T> apply)
    {
        CancellationTokenSource source;
        long version;
        lock (_sync)
        {
            if (_disposed) return;
            _current?.Cancel(); _current = source = new(); version = ++_version;
        }
        bool entered = false;
        try
        {
            await _serial.WaitAsync(source.Token);
            entered = true;
            var result = await Task.Run(() => compute(source.Token), source.Token);
            lock (_sync)
                if (!_disposed && version == _version && !source.IsCancellationRequested) apply(result);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception) when (IsObsolete(version)) { }
        finally
        {
            if (entered) _serial.Release();
            lock (_sync) { if (ReferenceEquals(_current, source)) _current = null; }
            source.Dispose();
        }
    }
    private bool IsObsolete(long version) { lock (_sync) return _disposed || version != _version; }
    public void Dispose()
    {
        lock (_sync) { _disposed = true; _version++; _current?.Cancel(); }
    }
}
