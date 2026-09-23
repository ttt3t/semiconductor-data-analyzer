namespace SemiconductorCsvAnalyzer.Models;

public sealed class DeviceInfo
{
    public string WaferId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public string Site { get; set; } = "";
    public string Result { get; set; } = "";
    public string SBin { get; set; } = "";
    public string HBin { get; set; } = "";
}
