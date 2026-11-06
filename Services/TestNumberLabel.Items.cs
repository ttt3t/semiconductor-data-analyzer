using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class TestNumberLabel
{
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
