using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Shared frozen colors: no brushes allocated while recycling table cells.</summary>
public sealed class YieldHeatBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette = Enumerable.Range(0, 101).Select(percent =>
    {
        var low = Color.FromRgb(254, 202, 202);
        var middle = Color.FromRgb(254, 243, 199);
        var high = Color.FromRgb(187, 247, 208);
        var from = percent <= 50 ? low : middle;
        var to = percent <= 50 ? middle : high;
        double fraction = percent <= 50 ? percent / 50d : (percent - 50) / 50d;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * fraction);
        var brush = new SolidColorBrush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TestItemSummary { Count: > 0 } summary && double.IsFinite(summary.InSpecPercent)
            ? Palette[(int)Math.Round(Math.Clamp(summary.InSpecPercent, 0, 100))]
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
