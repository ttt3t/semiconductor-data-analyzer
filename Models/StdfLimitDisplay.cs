namespace SemiconductorCsvAnalyzer.Models;

// Presentation metadata only. Value is the unshifted R*4 limit in its source units;
// UnitFactor follows unit merges without touching the original comparison boundary.
public sealed record StdfLimitDisplay(double Value, string Format, int Scale, double UnitFactor = 1)
{
    public StdfLimitDisplay ConvertUnits(double factor) => this with { UnitFactor = UnitFactor * factor };
}
