namespace SemiconductorCsvAnalyzer.Services;

public sealed partial class CsvParseResult
{
    public string SerialNumberNotice { get; set; } = "";
    public int ReassignedSerialNumberCount { get; set; }
    public bool SerialNumbersRenumbered => ReassignedSerialNumberCount > 0;
}
