using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Models;

public sealed class TestItemData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TestItemFocus Focus { get; set; } = new();
    public int ColumnIndex { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TestNumber { get; set; } = "";
    public string SBin { get; set; } = "";
    public string HBin { get; set; } = "";
    public string Unit { get; set; } = "";
    // Presentation only. Raw columns, thresholds and UUID identities keep source units.
    public bool IsStdfMeasurement { get; set; }
    public double DisplayFactor { get; set; } = 1;
    public string DisplayUnit { get; set; } = "";
    public string ShownUnit => DisplayUnit.Length > 0 ? DisplayUnit : Unit;
    public double? DisplayLowLimit => LowLimit * DisplayFactor;
    public double? DisplayHighLimit => HighLimit * DisplayFactor;
    public double? LowLimit { get; set; }
    public double? HighLimit { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public TestValueColumn Values { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public RecordList Errors { get; } = new();
}
