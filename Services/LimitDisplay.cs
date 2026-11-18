using System.Globalization;
using System.Text.RegularExpressions;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class LimitDisplay
{
    // Accept one bounded printf floating-point conversion, never execute a format string.
    private static readonly Regex Printf = new(@"^%[-+ 0#]*[0-9]{0,3}(?:\.([0-9]{1,2}))?l?([eEfFgG])$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static LimitPresentation Present(TestItemData item, bool upper, AnalyzerConfig config,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double? boundary = upper ? item.DisplayHighLimit : item.DisplayLowLimit;
        if (boundary is not double actual || !double.IsFinite(actual)) return new("-", null, null);
        var metadata = upper ? item.StdfHighLimitDisplay : item.StdfLowLimitDisplay;
        // A mixed-source merge can retain STDF samples but adopt CSV thresholds.
        // Follow the origin of this limit, rather than the origin of the samples.
        if (metadata == null)
            return new(actual.ToString($"F{config.DecimalPlaces}", culture), actual, null);

        double shown = metadata.Value * metadata.UnitFactor * item.DisplayFactor;
        string format = "G" + Math.Clamp(config.StdfLimitSignificantDigits, 1, 9);
        if (TryFormat(metadata.Format, out char kind, out int precision))
        {
            if (char.ToUpperInvariant(kind) == 'F')
            {
                double sourceScale = Math.Pow(10, metadata.Scale);
                double scaled = metadata.Value * sourceScale;
                double rounded = double.Parse(scaled.ToString("F" + precision, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                double conversion = metadata.UnitFactor * item.DisplayFactor / sourceScale;
                shown = rounded * conversion;
                double power = Math.Log10(conversion);
                int decimals = precision - (int)Math.Round(power);
                // A decimal format can be translated exactly across SI prefix changes.
                format = double.IsFinite(power) && Math.Abs(power - Math.Round(power)) < 1e-9 && decimals is >= 0 and <= 15
                    ? "F" + decimals : "G15";
            }
            else format = kind + precision.ToString(CultureInfo.InvariantCulture);
        }
        if (!double.IsFinite(shown)) { shown = actual; format = "G" + Math.Clamp(config.StdfLimitSignificantDigits, 1, 9); }
        string text = shown.ToString(format, CultureInfo.InvariantCulture);
        double reportValue = double.Parse(text, CultureInfo.InvariantCulture);
        if (reportValue == 0) text = text.TrimStart('-');
        string mantissa = text.Split('E', 'e')[0];
        int dot = mantissa.IndexOf('.');
        int places = dot < 0 ? 0 : mantissa.Length - dot - 1;
        string excel = "0" + (places > 0 ? "." + new string('0', places) : "") +
            (text.IndexOfAny(new[] { 'E', 'e' }) >= 0 ? "E+00" : "");
        return new(text.Replace(".", culture.NumberFormat.NumberDecimalSeparator), reportValue, excel);
    }

    private static bool TryFormat(string text, out char kind, out int precision)
    {
        kind = 'G'; precision = 6;
        if (text.Length > 64) return false;
        var match = Printf.Match(text.Trim());
        if (!match.Success) return false;
        kind = match.Groups[2].Value[0];
        if (match.Groups[1].Success) precision = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (precision > 15) return false;
        if (char.ToUpperInvariant(kind) == 'G' && precision == 0) precision = 1;
        return true;
    }
}
