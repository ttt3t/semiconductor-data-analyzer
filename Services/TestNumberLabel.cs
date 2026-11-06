using System.Globalization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class TestNumberLabel
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

    internal static void Disambiguate(IEnumerable<TestItemData> items)
    {
        foreach (var group in items.GroupBy(i => i.BaseTestNumber)
            .Where(g => g.Skip(1).Any() && g.Any(i => i.IsStdfMeasurement || IsQualified(i.TestNumber))))
        {
            int index = 0;
            foreach (var item in group.OrderBy(i => i.Name, StringComparer.Ordinal)
                .ThenBy(i => i.LowLimit).ThenBy(i => i.HighLimit).ThenBy(i => i.Unit, StringComparer.Ordinal))
            {
                bool prefixed = item.DisplayName == $"{item.TestNumber}_{item.Name}";
                item.TestNumber = $"{group.Key}[{++index}]";
                if (prefixed) item.DisplayName = $"{item.TestNumber}_{item.Name}";
            }
        }
    }
}
