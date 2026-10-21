using System.Globalization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class MeasurementDisplay
{
    // SI prefixes are case sensitive: mHz is millihertz, MHz is megahertz.
    private static readonly (int Power, string Prefix)[] Prefixes =
        { (-15, "f"), (-12, "p"), (-9, "n"), (-6, "u"), (-3, "m"), (0, ""), (3, "k"), (6, "M"), (9, "G"), (12, "T") };

    public static void Configure(IEnumerable<TestItemData> items)
    {
        foreach (var item in items)
        {
            AnalysisOperation.CheckCancellation();
            if (!item.IsStdfMeasurement || item.Values.Count == 0) continue;
            string unit = item.Unit.Trim();
            // Already prefixed units and other quantities retain their original representation.
            if (unit is not ("A" or "V" or "s" or "S" or "Hz")) continue;
            double mean = 0;
            int count = 0;
            foreach (double number in item.Values.ReadNumbers())
            {
                if ((count & 4095) == 0) AnalysisOperation.CheckCancellation();
                mean += (number - mean) / ++count;
            }
            double magnitude = Math.Abs(mean);
            int power = 0;
            if (double.IsFinite(magnitude) && magnitude > 0)
            {
                while (magnitude < .1 && power > -15) { magnitude *= 1000; power -= 3; }
                while (magnitude >= 1000 && power < 12) { magnitude /= 1000; power += 3; }
            }
            item.DisplayFactor = Math.Pow(10, -power);
            item.DisplayUnit = Prefixes.First(p => p.Power == power).Prefix + unit;
        }
    }

    public static string ExportText(TestItemData item, string raw) => item.DisplayFactor != 1 && DatasetService.TryValue(raw, out double value)
        ? (value * item.DisplayFactor).ToString("R", CultureInfo.InvariantCulture) : raw;

    public static double? ConversionFactor(string from, string to)
    {
        (string Unit, int Power)? Parse(string text)
        {
            foreach (string unit in new[] { "Hz", "A", "V", "s", "S" })
                if (text.EndsWith(unit, StringComparison.Ordinal))
                {
                    string prefix = text[..^unit.Length].Replace("µ", "u").Replace("μ", "u");
                    foreach (var option in Prefixes) if (prefix == option.Prefix) return (unit, option.Power);
                }
            return null;
        }
        var a = Parse(from); var b = Parse(to);
        return a.HasValue && b.HasValue && a.Value.Unit == b.Value.Unit ? Math.Pow(10, a.Value.Power - b.Value.Power) : null;
    }
}
