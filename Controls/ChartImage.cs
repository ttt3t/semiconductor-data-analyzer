using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SemiconductorCsvAnalyzer.Controls;

// Logical size controls layout; DPI controls PNG pixels without changing the chart's proportions.
public readonly record struct ChartImageSize(double Width, double Height, double DpiX = 96, double DpiY = 96)
{
    public int PixelWidth => checked((int)Math.Ceiling(Width * DpiX / 96));
    public int PixelHeight => checked((int)Math.Ceiling(Height * DpiY / 96));
    public void Validate()
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0 ||
            !double.IsFinite(DpiX) || !double.IsFinite(DpiY) || DpiX <= 0 || DpiY <= 0 ||
            Width * DpiX / 96 > 16384 || Height * DpiY / 96 > 16384 ||
            Width * DpiX / 96 * Height * DpiY / 96 > 64_000_000)
            throw new ArgumentOutOfRangeException(nameof(Width), "图表尺寸无效或过大，请缩小窗口后重试。");
    }
}

public static class ChartImage
{
    public static ChartImageSize SizeOf(FrameworkElement chart)
    {
        var dpi = VisualTreeHelper.GetDpi(chart);
        return new(chart.ActualWidth, chart.ActualHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY);
    }

    public static BitmapSource Capture(FrameworkElement chart, ChartImageSize size)
    {
        size.Validate();
        chart.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size.PixelWidth, size.PixelHeight, size.DpiX, size.DpiY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        var bounds = new Rect(0, 0, size.Width, size.Height);
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, bounds);
            // Rendering a child visual directly includes its DockPanel layout offset and
            // crops the bottom. Sample its local coordinates, without touching live layout.
            // Relative viewbox follows the visual's own bounds, including its layout offset.
            // An absolute (0,0) viewbox would still include the parent's header space.
            var brush = new VisualBrush(chart) { Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, bounds);
        }
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    public static MemoryStream Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    public static void Copy(FrameworkElement chart)
    {
        chart.UpdateLayout();
        var bitmap = Capture(chart, SizeOf(chart));
        using var png = Png(bitmap);
        var data = new DataObject();
        data.SetData("PNG", png, false);
        // Excel/Paint can use the bitmap fallback; image-aware applications receive real PNG bytes.
        data.SetImage(bitmap);
        Clipboard.SetDataObject(data, true);
    }
}
