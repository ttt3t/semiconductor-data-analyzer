namespace SemiconductorCsvAnalyzer.Models;

public sealed class StatisticsResult
{
    public int Count { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int BelowLsl { get; set; }
    public int AboveUsl { get; set; }
    public int InSpec { get; set; }
    public double InSpecPercent { get; set; }
    public double? Cp { get; set; }
    public double? Cpk { get; set; }
    public double? Cpu { get; set; }
    public double? Cpl { get; set; }
}
