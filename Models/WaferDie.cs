using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Models;

public sealed class WaferDie
{
    public string WaferId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string SBin { get; set; } = "";
    public string HBin { get; set; } = "";
    public string Result { get; set; } = "";
    public string Unit { get; set; } = "";
    public string RawValue { get; set; } = "";
    public string AdditionalInfo { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Site { get; set; } = "";
    public string Category { get; set; } = "";
    public double? Value { get; set; }
    public Brush Color { get; set; } = Brushes.Gray;

    public WaferDie WithColor(Brush color)
    {
        var copy = (WaferDie)MemberwiseClone();
        copy.Color = color;
        return copy;
    }
}
