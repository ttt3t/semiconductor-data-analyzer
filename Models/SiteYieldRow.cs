namespace SemiconductorCsvAnalyzer.Models;

public sealed class SiteYieldRow
{
    public string Site { get; set; } = "";
    public int Total { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public double YieldPercent { get; set; }
}
