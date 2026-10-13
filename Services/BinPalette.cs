using System.Globalization;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Fifty frozen BIN colors; green is reserved for BIN 1 and P/F pass.</summary>
public static class BinPalette
{
    private static readonly Brush[] Colors = CreatePalette();
    private static readonly Brush BinOneGreen = MakeGreen();
    private static Brush MakeGreen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(46, 160, 67)); brush.Freeze(); return brush;
    }
    private static bool IsBinOne(string category)
    {
        string text = category.Trim().ToUpperInvariant();
        foreach (string prefix in new[] { "SBIN", "HBIN", "BIN" })
            if (text.StartsWith(prefix)) { text = text[prefix.Length..]; break; }
        return BinStatisticsService.NormalizeBin(text) == "1";
    }

    public static string Key(string category) => string.IsNullOrWhiteSpace(category) ? "(空)" : category;

    public static IReadOnlyDictionary<string, Brush> Create(IEnumerable<string> categories)
    {
        // Use all wafers and raw attempts, so wafer/retest selection cannot shift the legend colors.
        // Rank distinct bins instead of taking bin number % 50: arbitrary IDs must not collide.
        var result = new Dictionary<string, Brush>(StringComparer.Ordinal);
        int index = 0;
        foreach (string category in categories.Select(Key).Distinct(StringComparer.Ordinal)
            .OrderBy(NumericText.SortKey).ThenBy(s => s, StringComparer.Ordinal))
            result[category] = IsBinOne(category) ? BinOneGreen : Colors[index++ % Colors.Length];
        return result;
    }

    public static Brush ColorForCategory(string category)
    {
        if (IsBinOne(category)) return BinOneGreen;
        if (long.TryParse(category, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bin))
            return Colors[(int)((bin % Colors.Length + Colors.Length - 1) % Colors.Length)];
        uint hash = 2166136261;
        foreach (char c in category) hash = unchecked((hash ^ c) * 16777619);
        return Colors[hash % Colors.Length];
    }

    private static Brush[] CreatePalette()
    {
        // Alternate hue families before changing lightness; exclude yellow-green through teal.
        double[] hues = { 215, 8, 275, 38, 240, 325, 200, 355, 300, 25 };
        (double Saturation, double Lightness)[] shades = { (.80, .46), (.72, .64), (.70, .31), (.95, .55), (.50, .49) };
        return shades.SelectMany(shade => hues.Select(hue =>
        {
            double c = (1 - Math.Abs(2 * shade.Lightness - 1)) * shade.Saturation;
            double h = hue / 60, x = c * (1 - Math.Abs(h % 2 - 1)), m = shade.Lightness - c / 2;
            var (r, g, b) = h switch
            {
                < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x),
                < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x)
            };
            var brush = new SolidColorBrush(Color.FromRgb((byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255)));
            brush.Freeze();
            return (Brush)brush;
        })).ToArray();
    }
}
