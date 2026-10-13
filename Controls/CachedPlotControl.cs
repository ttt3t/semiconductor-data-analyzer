using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>One bitmap per current plot. Resize previews stretch it; viewport/data changes redraw from data.</summary>
public abstract class CachedPlotControl : FrameworkElement
{
    private readonly DispatcherTimer _resizeTimer;
    private BitmapSource? _plotBitmap;
    private Size _bitmapSize, _previewSize;
    private DpiScale _bitmapDpi;
    private bool _plotDirty = true, _resizePending, _useBitmapCache = true;
    private const double MaxBitmapPixels = 8_000_000;

    protected CachedPlotControl()
    {
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizePending = false;
            InvalidateVisual();
        };
        Unloaded += (_, _) => ReleasePlotBitmap();
        Loaded += (_, _) => InvalidatePlot();
    }

    // Export renders vectors directly at the report's target DPI, avoiding an upscaled screen image.
    public bool UseBitmapCache
    {
        get => _useBitmapCache;
        set
        {
            if (_useBitmapCache == value) return;
            _useBitmapCache = value;
            ReleasePlotBitmap();
            InvalidateVisual();
        }
    }

    protected virtual bool HasPlotContent => true;
    protected bool IsResizePreview => UseBitmapCache && _resizePending;
    protected abstract void DrawPlot(DrawingContext dc);
    protected virtual void OnResizePreview() { }

    protected void InvalidatePlot()
    {
        _plotDirty = true;
        _resizeTimer.Stop();
        _resizePending = false;
        if (!HasPlotContent) _plotBitmap = null;
        InvalidateVisual();
    }

    private void ReleasePlotBitmap()
    {
        _resizeTimer.Stop();
        _resizePending = false;
        _plotBitmap = null;
        _bitmapSize = default;
        _previewSize = default;
        _plotDirty = true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Arrange runs before OnRender; SizeChanged is too late to prevent that first expensive redraw.
        if (UseBitmapCache && _plotBitmap != null && HasPlotContent &&
            _bitmapSize != finalSize && _previewSize != finalSize)
        {
            _previewSize = finalSize;
            _resizePending = true;
            _resizeTimer.Stop();
            _resizeTimer.Start();
            OnResizePreview();
            InvalidateVisual();
        }
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidatePlot();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!UseBitmapCache || !HasPlotContent)
        {
            DrawPlot(dc);
            return;
        }
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_plotBitmap == null || _plotDirty || !_bitmapDpi.Equals(dpi) || !_resizePending && _bitmapSize != size)
        {
            // Limit the current bitmap even on very large/high-DPI monitors. No item/history cache.
            double scale = Math.Min(1, Math.Sqrt(MaxBitmapPixels /
                (size.Width * dpi.DpiScaleX * size.Height * dpi.DpiScaleY)));
            scale = Math.Min(scale, Math.Min(8192 / (size.Width * dpi.DpiScaleX), 8192 / (size.Height * dpi.DpiScaleY)));
            int width = Math.Clamp((int)Math.Ceiling(size.Width * dpi.DpiScaleX * scale), 1, 8192);
            int height = Math.Clamp((int)Math.Ceiling(size.Height * dpi.DpiScaleY * scale), 1, 8192);
            height = Math.Min(height, (int)(MaxBitmapPixels / width));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) DrawPlot(drawing);
            var next = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX * scale,
                96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
            next.Render(visual);
            next.Freeze();
            // Retain pixels only, not thousands of per-point drawing commands.
            using (visual.RenderOpen()) { }
            _plotBitmap = next;
            _bitmapSize = size;
            _previewSize = size;
            _bitmapDpi = dpi;
            _plotDirty = false;
        }
        dc.DrawImage(_plotBitmap, new Rect(new Point(), size));
    }
}
