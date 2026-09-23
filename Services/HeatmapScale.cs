using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class HeatmapScale
{
    private static readonly SolidColorBrush[] Palette = CreatePalette();
    public double Minimum { get; }
    public double Mean { get; }
    public double Sigma { get; }
    public double Maximum { get; }
    public double DataMinimum { get; }
    public double DataMaximum { get; }

    private HeatmapScale(double minimum, double mean, double maximum, double sigma,
        double dataMinimum, double dataMaximum)
    {
        Minimum = minimum; Mean = mean; Maximum = maximum; Sigma = sigma;
        DataMinimum = dataMinimum; DataMaximum = dataMaximum;
    }

    public static HeatmapScale? Create(IEnumerable<double> values)
    {
        var samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0) return null;
        double min = samples.Min(), max = samples.Max(), scale = Math.Max(Math.Abs(min), Math.Abs(max));
        // Normalize first so finite, very large values do not overflow the mean/variance.
        double mean = scale == 0 ? 0 : samples.Average(v => v / scale);
        double variance = scale == 0 ? 0 : samples.Average(v => Math.Pow(v / scale - mean, 2));
        return new(min, Math.Clamp(mean * scale, min, max), max,
            Math.Sqrt(Math.Min(1, Math.Max(0, variance))) * scale, min, max);
    }

    public HeatmapScale WithSigmaRange(double? multiplier)
    {
        if (!multiplier.HasValue) return new(DataMinimum, Mean, DataMaximum, Sigma, DataMinimum, DataMaximum);
        if (!double.IsFinite(multiplier.Value) || multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
        double radius = Sigma * multiplier.Value;
        double lower = Math.Max(double.MinValue, Mean - radius), upper = Math.Min(double.MaxValue, Mean + radius);
        return new(lower, Mean, upper, Sigma, DataMinimum, DataMaximum);
    }

    public Brush ColorForValue(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value)) return Brushes.Gray;
        double v = value.Value;
        if (v == Mean) return Palette[256];
        if (v <= Minimum) return Palette[0];
        if (v >= Maximum) return Palette[512];
        // Independent sides keep the mean green in both sigma and min/max views.
        double span = v < Mean ? Mean - Minimum : Maximum - Mean;
        double ratio = double.IsFinite(span) ? (v - Mean) / span :
            (v < Mean
                ? (v * 0.5 - Mean * 0.5) / (Mean * 0.5 - Minimum * 0.5)
                : (v * 0.5 - Mean * 0.5) / (Maximum * 0.5 - Mean * 0.5));
        int index = (int)Math.Round(256 + 256 * Math.Clamp(ratio, -1, 1));
        return Palette[Math.Clamp(index, 0, 512)];
    }

    private static SolidColorBrush[] CreatePalette()
    {
        var blue = Color.FromRgb(35, 95, 220);
        var green = Color.FromRgb(46, 160, 67);
        var red = Color.FromRgb(220, 55, 55);
        var brushes = new SolidColorBrush[513];
        for (int i = 0; i < brushes.Length; i++)
        {
            Color from = i <= 256 ? blue : green, to = i <= 256 ? green : red;
            double t = i <= 256 ? i / 256.0 : (i - 256) / 256.0;
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
            var brush = new SolidColorBrush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }
}
