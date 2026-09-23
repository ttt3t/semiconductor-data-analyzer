using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Coalesces scrolling input while keeping the native thumb responsive.</summary>
public class FastDataGrid : DataGrid
{
    private ScrollViewer? _scrollViewer;
    private DispatcherOperation? _scrollOperation;
    private DispatcherOperation? _thumbOperation;
    private double _thumbOffset;
    private Orientation _thumbOrientation;
    private int _wheelDelta;
    private int _pendingRows;
    private readonly DispatcherTimer _scrollIdleTimer;
    private bool _thumbDragging;

    public bool IsScrollActive { get; private set; }
    public event EventHandler? ScrollActivityChanged;

    public FastDataGrid()
    {
        // Native deferred scrolling moves the thumb immediately. Commit the latest
        // content offset at background priority so queued mouse input runs first.
        ScrollViewer.SetIsDeferredScrollingEnabled(this, true);
        Loaded += (_, _) =>
        {
            KeepColumnHeadersRealized();
            if (!IsScrollActive) RestoreDeferredAutomation(this);
        };
        AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, e) =>
        {
            if (e.ScrollEventType != ScrollEventType.ThumbTrack ||
                e.OriginalSource is not ScrollBar bar || _scrollViewer == null ||
                !ReferenceEquals(bar.TemplatedParent, _scrollViewer) ||
                !ScrollViewer.GetIsDeferredScrollingEnabled(this)) return;
            _thumbOffset = e.NewValue;
            _thumbOrientation = bar.Orientation;
            if (_thumbOperation is not { Status: DispatcherOperationStatus.Pending })
                _thumbOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_scrollViewer == null) return;
                    if (_thumbOrientation == Orientation.Vertical)
                        _scrollViewer.ScrollToVerticalOffset(_thumbOffset);
                    else _scrollViewer.ScrollToHorizontalOffset(_thumbOffset);
                }));
        }), handledEventsToo: true);
        _scrollIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(80) };
        _scrollIdleTimer.Tick += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            if (!_thumbDragging) SetScrollActive(false);
        };
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _scrollViewer) &&
                (e.VerticalChange != 0 || e.HorizontalChange != 0)) MarkScrollActivity();
        }), handledEventsToo: true);
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, e) =>
        {
            if (!IsScrollThumb(e.OriginalSource as DependencyObject)) return;
            _thumbOperation?.Abort();
            _thumbDragging = true;
            MarkScrollActivity();
        }), handledEventsToo: true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (!IsScrollThumb(e.OriginalSource as DependencyObject)) return;
            // ScrollBar commits the final position itself before this event reaches us.
            _thumbOperation?.Abort();
            _thumbDragging = false;
            MarkScrollActivity();
        }), handledEventsToo: true);
        Unloaded += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            _scrollOperation?.Abort();
            _thumbOperation?.Abort();
            _pendingRows = _wheelDelta = 0;
            _thumbDragging = false;
            SetScrollActive(false);
        };
    }

    private static bool IsScrollThumb(DependencyObject? source)
    {
        while (source is Visual)
        {
            if (source is ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void MarkScrollActivity()
    {
        SetScrollActive(true);
        _scrollIdleTimer.Stop();
        if (!_thumbDragging) _scrollIdleTimer.Start();
    }

    private void SetScrollActive(bool active)
    {
        if (IsScrollActive == active) return;
        IsScrollActive = active;
        if (!active && IsLoaded) RestoreDeferredAutomation(this);
        ScrollActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override DependencyObject GetContainerForItemOverride() => new ScrollingDataGridRow(this);

    private static void RestoreDeferredAutomation(DependencyObject root)
    {
        // Visit realized rows only, and never create peers just for scrolling.
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridRow row)
            {
                if (UIElementAutomationPeer.FromElement(row) is ScrollingRowPeer peer)
                    peer.RestoreChildren();
            }
            else RestoreDeferredAutomation(child);
        }
    }

    private sealed class ScrollingDataGridRow(FastDataGrid grid) : DataGridRow
    {
        // The main table has no row headers/details. Keep the native row peer for
        // other templates: WPF's row-header provider requires that exact peer type.
        protected override AutomationPeer OnCreateAutomationPeer() =>
            grid.HeadersVisibility == DataGridHeadersVisibility.Column &&
            grid.RowDetailsTemplate == null && grid.RowDetailsTemplateSelector == null
                ? new ScrollingRowPeer(this, grid) : base.OnCreateAutomationPeer();
    }

    private sealed class ScrollingRowPeer(DataGridRow row, FastDataGrid grid) : FrameworkElementAutomationPeer(row)
    {
        private readonly DataGridRowAutomationPeer _nativePeer = new(row);
        private bool _childrenDeferred;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
        protected override string GetClassNameCore() => nameof(DataGridRow);
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            // Popup/OS automation requests can activate this tree long after startup.
            // Recycling rows then repeatedly rebuilds every cell's automation subtree.
            // During a mouse-thumb drag keep the native grid/row/scroll providers, but
            // publish cell children once scrolling settles. Visuals still update live.
            if (grid._thumbDragging)
            {
                _childrenDeferred = true;
                return null;
            }
            _childrenDeferred = false;
            // DataGridRowAutomationPeer is sealed; delegate the normal cell tree to it.
            _nativePeer.EventsSource = EventsSource;
            _nativePeer.ResetChildrenCache();
            return _nativePeer.GetChildren();
        }

        internal void RestoreChildren()
        {
            if (!_childrenDeferred) return;
            // Refresh the item peer (the row peer is only its visual wrapper).
            // Required even if releasing the thumb did not change the final offset.
            var peer = EventsSource ?? this;
            peer.ResetChildrenCache();
            peer.InvalidatePeer();
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scrollViewer = FindScrollViewer(this);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(KeepColumnHeadersRealized));
    }

    private void KeepColumnHeadersRealized()
    {
        // Virtualize data cells, but keep the small header/filter strip alive when
        // resizing columns or the splitter. A local value overrides the grid default.
        KeepHeaders(this);
        static void KeepHeaders(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is DataGridColumnHeadersPresenter headers)
                    VirtualizingPanel.SetIsVirtualizing(headers, false);
                else KeepHeaders(child);
            }
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_scrollViewer == null) { base.OnPreviewMouseWheel(e); return; }
        MarkScrollActivity();
        _wheelDelta += e.Delta;
        _pendingRows -= _wheelDelta / Mouse.MouseWheelDeltaForOneLine;
        _wheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        if (_scrollOperation is not { Status: DispatcherOperationStatus.Pending })
            _scrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                int rows = _pendingRows;
                _pendingRows = 0;
                if (rows != 0 && _scrollViewer != null)
                    _scrollViewer.ScrollToVerticalOffset(Math.Clamp(
                        _scrollViewer.VerticalOffset + rows, 0, _scrollViewer.ScrollableHeight));
            }));
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
