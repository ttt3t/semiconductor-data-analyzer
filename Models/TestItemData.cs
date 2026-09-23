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
    public double? LowLimit { get; set; }
    public double? HighLimit { get; set; }
    public TestValueColumn Values { get; } = new();
    public List<CsvRecord> Errors { get; } = new();
}
