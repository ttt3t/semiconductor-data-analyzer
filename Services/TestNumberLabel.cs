using System.Globalization;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class TestNumberLabel
{
    public static bool IsQualified(string text)
    {
        int bracket = text.LastIndexOf('[');
        return bracket > 0 && text.EndsWith(']') &&
            ulong.TryParse(text.AsSpan(0, bracket), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            int.TryParse(text.AsSpan(bracket + 1, text.Length - bracket - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0;
    }

    public static string Base(string text)
    {
        text = text.Trim();
        return IsQualified(text) ? text[..text.LastIndexOf('[')] : text;
    }

}
