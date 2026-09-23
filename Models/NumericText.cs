using System.Globalization;

namespace SemiconductorCsvAnalyzer.Models;

internal static class NumericText
{
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string s = text.Trim();
        if (s == "-" || s == "(空)" || s.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return null;

        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands;
        if (double.TryParse(s, styles, CultureInfo.InvariantCulture, out double v))
            return v;
        if (double.TryParse(s, styles, CultureInfo.CurrentCulture, out v))
            return v;
        return null;
    }

    /// <summary>用于排序：能解析则用有符号数值，否则排到最后。</summary>
    public static double SortKey(string? text) => Parse(text) ?? double.PositiveInfinity;

    public static double SortKey(double? value) => value ?? double.PositiveInfinity;
}
