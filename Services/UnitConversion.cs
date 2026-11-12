using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Only known linear SI conversions are inferred. Prefix case remains significant.</summary>
public static class UnitConversion
{
    private static readonly (string Prefix, int Power)[] Prefixes =
    { ("f", -15), ("p", -12), ("n", -9), ("u", -6), ("m", -3), ("c", -2), ("d", -1),
      ("", 0), ("da", 1), ("h", 2), ("k", 3), ("M", 6), ("G", 9), ("T", 12) };
    private static readonly string[] Dimensions = { "Hz", "Pa", "Ω", "A", "V", "s", "S", "F", "H", "W" };
    private static readonly (string Alias, string Symbol)[] Aliases =
    { ("amperes", "A"), ("ampere", "A"), ("amps", "A"), ("amp", "A"), ("volts", "V"), ("volt", "V"),
      ("seconds", "s"), ("second", "s"), ("secs", "s"), ("sec", "s"), ("siemens", "S"), ("hertz", "Hz"),
      ("ohms", "Ω"), ("ohm", "Ω"), ("farads", "F"), ("farad", "F"), ("henries", "H"), ("henry", "H"), ("watts", "W"), ("watt", "W") };

    public static string Normalize(string unit)
    {
        string text = unit.Trim().Replace('µ', 'u').Replace('μ', 'u').Replace('Ω', 'Ω');
        foreach (var (alias, symbol) in Aliases)
            if (text.EndsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                string prefix = text[..^alias.Length].TrimEnd();
                if (Prefixes.Any(p => p.Prefix == prefix)) return prefix + symbol;
            }
        return text;
    }

    public static double? Factor(string from, string to)
    {
        from = Normalize(from); to = Normalize(to);
        if (from == to) return 1;
        (string Dimension, int Power)? Parse(string text)
        {
            foreach (string dimension in Dimensions)
                if (text.EndsWith(dimension, StringComparison.Ordinal))
                {
                    string prefix = text[..^dimension.Length];
                    foreach (var option in Prefixes) if (prefix == option.Prefix) return (dimension, option.Power);
                }
            return null;
        }
        var a = Parse(from); var b = Parse(to);
        return a.HasValue && b.HasValue && a.Value.Dimension == b.Value.Dimension ? Math.Pow(10, a.Value.Power - b.Value.Power) : null;
    }

    private sealed record MergeDefinition(string Number, int Slot, string DisplayName, string Channel = "");
    internal static string ExportMerge(TestItemData item) => item.UnitMergeSlot <= 0 ? "" :
        JsonSerializer.Serialize(new MergeDefinition(item.UnitMergeNumber ?? item.TestNumber, item.UnitMergeSlot, item.UnitMergeDisplayName ?? item.Name, item.StdfChannel));
    internal static void ReadMerge(TestItemData item, string text)
    {
        if (text.Length == 0) return;
        try
        {
            var definition = JsonSerializer.Deserialize<MergeDefinition>(text);
            if (definition == null || definition.Slot <= 0 || definition.Number == null || definition.DisplayName == null)
                throw new JsonException("单位分项定义无效。");
            item.UnitMergeNumber = definition.Number; item.UnitMergeSlot = definition.Slot; item.UnitMergeDisplayName = definition.DisplayName;
            item.StdfChannel = definition.Channel ?? "";
        }
        catch (JsonException ex) { throw new System.IO.InvalidDataException("整合 CSV 的单位分项定义损坏。", ex); }
    }
}
