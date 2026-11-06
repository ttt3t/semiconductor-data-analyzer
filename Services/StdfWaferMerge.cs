namespace SemiconductorCsvAnalyzer.Services;

internal readonly record struct StdfWaferIdentity(string Lot, string Wafer);

/// <summary>Small per-wafer identity maps; no additional file scan or per-chip metadata.</summary>
internal static class StdfWaferMerge
{
    internal static readonly IReadOnlyDictionary<string, StdfWaferIdentity> Empty = new Dictionary<string, StdfWaferIdentity>();

    internal static IReadOnlyDictionary<string, StdfWaferIdentity> ReadOrigins(StdfFileMetadata metadata, IReadOnlyList<string> wafers)
    {
        if (metadata.LotId.Length == 0) return Empty;
        var present = wafers.ToHashSet(StringComparer.Ordinal);
        return metadata.WaferIds.Where(present.Contains).ToDictionary(w => w, w => new StdfWaferIdentity(metadata.LotId, w), StringComparer.Ordinal);
    }

    internal static Dictionary<StdfWaferIdentity, string> Targets(CsvParseResult existing)
        => existing.StdfWaferOrigins.GroupBy(p => p.Value).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Key);

    internal static IReadOnlyDictionary<string, StdfWaferIdentity> Merge(CsvParseResult existing, CsvParseResult incoming,
        IReadOnlyDictionary<string, string> waferMap)
    {
        var origins = new Dictionary<string, StdfWaferIdentity>(existing.StdfWaferOrigins, StringComparer.Ordinal);
        var present = existing.Wafers.ToHashSet(StringComparer.Ordinal);
        foreach (var (original, target) in waferMap)
        {
            bool known = incoming.StdfWaferOrigins.TryGetValue(original, out var identity);
            if (present.Contains(target))
            {
                // Manually merging unrelated or unidentified wafers must not leave a false identity.
                if (!known || !origins.TryGetValue(target, out var old) || old != identity) origins.Remove(target);
            }
            else
            {
                if (known) origins[target] = identity;
                present.Add(target);
            }
        }
        return origins;
    }
}
