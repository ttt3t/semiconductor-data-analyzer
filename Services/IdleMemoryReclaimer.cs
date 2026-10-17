using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Services;

// Large column arrays and WPF drawing resources are released at lifecycle boundaries.
// Debounce requests and wait for input/calculation to stop before shrinking the heap.
internal sealed class IdleMemoryReclaimer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _canCollect;
    private long _lastActivity;
    private static long _lastCollection;
    private static long _lastAllocated = GC.GetTotalAllocatedBytes();
    private static readonly object CollectionGate = new();
    private bool _pending, _running, _disposed;
    internal int CollectionCount { get; private set; }

    public IdleMemoryReclaimer(Dispatcher dispatcher, Func<bool> canCollect)
    {
        _canCollect = canCollect;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
    }

    public void Request()
    {
        if (_disposed) return;
        _lastActivity = Stopwatch.GetTimestamp();
        if (!_pending)
        {
            _pending = true;
            InputManager.Current.PreProcessInput += OnInput;
        }
        _timer.Start();
    }

    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs or TextCompositionEventArgs)
            _lastActivity = Stopwatch.GetTimestamp();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _running || !_pending) return;
        if (!_canCollect() || Mouse.Captured != null || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed)
        {
            _lastActivity = Stopwatch.GetTimestamp();
            return;
        }
        if (Stopwatch.GetElapsedTime(_lastActivity).TotalSeconds < 5) return;

        StopWaiting();
        _running = true;
        try
        {
            bool collected = await Task.Run(() =>
            {
                lock (CollectionGate)
                {
                    if (_disposed || _lastCollection != 0 && Stopwatch.GetElapsedTime(_lastCollection).TotalSeconds < 60) return false;
                    long allocated = GC.GetTotalAllocatedBytes();
                    if (allocated - _lastAllocated < 128L * 1024 * 1024) return false;
                    // Let the runtime decide whether a background collection is worthwhile.
                    // Never compact twice or wait for finalizers on every UI operation.
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
                    _lastAllocated = allocated; _lastCollection = Stopwatch.GetTimestamp(); return true;
                }
            });
            if (collected) CollectionCount++;
        }
        finally
        {
            _running = false;
        }
    }

    private void StopWaiting()
    {
        _timer.Stop();
        if (_pending) InputManager.Current.PreProcessInput -= OnInput;
        _pending = false;
    }

    public void Dispose()
    {
        _disposed = true;
        StopWaiting();
        _timer.Tick -= OnTick;
    }
}
