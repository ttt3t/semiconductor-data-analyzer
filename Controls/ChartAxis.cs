using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Controls;

public static class ChartAxis
{
    // Shared horizontal plotting bounds keep the charts aligned after splitter resizing.
    public const double PlotLeft = 52;
    public const double PlotRight = 16;
    private static readonly Typeface Typeface = new("Segoe UI");

    public static void DrawAxes(DrawingContext dc, Pen pen, Rect plot)
    {
        double xEnd = plot.Right + 7, yEnd = plot.Top - 7;
        dc.DrawLine(pen, new Point(plot.Left, plot.Bottom), new Point(xEnd, plot.Bottom));
        dc.DrawLine(pen, new Point(xEnd - 4, plot.Bottom - 3), new Point(xEnd, plot.Bottom));
        dc.DrawLine(pen, new Point(xEnd - 4, plot.Bottom + 3), new Point(xEnd, plot.Bottom));
        dc.DrawLine(pen, new Point(plot.Left, plot.Bottom), new Point(plot.Left, yEnd));
        dc.DrawLine(pen, new Point(plot.Left - 3, yEnd + 4), new Point(plot.Left, yEnd));
        dc.DrawLine(pen, new Point(plot.Left + 3, yEnd + 4), new Point(plot.Left, yEnd));
    }

    public static IReadOnlyList<double> Ticks(double min, double max, int target, bool integers = false)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) return Array.Empty<double>();
        double raw = (max - min) / Math.Clamp(target, 1, 20);
        if (!double.IsFinite(raw) || raw <= 0) return Array.Empty<double>();
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double residual = raw / magnitude;
        double step = (residual <= 1 ? 1 : residual <= 2 ? 2 : residual <= 5 ? 5 : 10) * magnitude;
        if (integers) step = Math.Max(1, step);
        if (!double.IsFinite(step) || step <= 0) return Array.Empty<double>();
        double first = Math.Ceiling(min / step) * step;
        var ticks = new List<double>();
        for (int i = 0; i < 64; i++)
        {
            double value = first + i * step;
            if (!double.IsFinite(value) || value > max + step * 1e-9) break;
            value = Math.Abs(value) < step * 1e-9 ? 0 : value;
            if (ticks.Count == 0 || value > ticks[^1]) ticks.Add(value);
        }
        return ticks;
    }

    public static string Format(double value, double step = 0)
    {
        if (value == 0) return "0";
        int precision = 6;
        if (step > 0 && double.IsFinite(step))
            precision = (int)Math.Clamp(Math.Ceiling(Math.Log10(Math.Abs(value)) - Math.Log10(step)) + 2, 6, 17);
        return value.ToString("G" + precision, CultureInfo.InvariantCulture);
    }

    public static FormattedText Text(Visual owner, string text, double size = 11, Brush? brush = null)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, size,
            brush ?? Brushes.Black, VisualTreeHelper.GetDpi(owner).PixelsPerDip);

    public static string ValueTitle(string? unit) => string.IsNullOrWhiteSpace(unit)
        ? "测量值（无单位）" : $"测量值（{unit.Trim()}）";

    public static void DrawTitle(DrawingContext dc, Visual owner, string text, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        var formatted = Text(owner, text);
        formatted.MaxTextWidth = area.Width;
        formatted.MaxTextHeight = area.Height;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(formatted, area.TopLeft);
    }
}
