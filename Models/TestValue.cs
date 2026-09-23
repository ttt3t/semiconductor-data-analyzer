namespace SemiconductorCsvAnalyzer.Models;

// A transient view of one column cell; datasets store doubles, not TestValue instances.
public struct TestValue
{
    private CsvRecord? _record;
    private bool _shared;
    private string? _testId;
    public TestValue() { }
    public TestValue(CsvRecord record, double value, string? testId = null)
    { _record = record; _shared = true; Value = value; _testId = testId; }
    internal CsvRecord Record => _record ?? new CsvRecord { Id = "" };
    public string RawText => _testId != null && _record != null
        ? _record.Measurements.GetValueOrDefault(_testId, "")
        : Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // Share the row's identity instead of copying nine fields into every measurement.
    // A caller editing a value's identity still gets the previous independent behavior.
    private CsvRecord EditableIdentity()
    {
        if (_record == null) _record = new CsvRecord { Id = "" };
        else if (_shared)
            _record = new CsvRecord
            {
                Id = _record.Id, WaferId = _record.WaferId, SerialNumber = _record.SerialNumber,
                OriginalSerialNumber = _record.OriginalSerialNumber,
                Site = _record.Site, Result = _record.Result, SBin = _record.SBin, HBin = _record.HBin,
                Product = _record.Product, Program = _record.Program, Lot = _record.Lot,
                X = _record.X, Y = _record.Y
            };
        _shared = false;
        return _record;
    }
    public string WaferId { get => _record?.WaferId ?? ""; set => EditableIdentity().WaferId = value; }
    public string RecordId { get => _record?.Id ?? ""; set => EditableIdentity().Id = value; }
    public string SerialNumber { get => _record?.SerialNumber ?? ""; set => EditableIdentity().SerialNumber = value; }
    public string Site { get => _record?.Site ?? ""; set => EditableIdentity().Site = value; }
    public string Result { get => _record?.Result ?? ""; set => EditableIdentity().Result = value; }
    public string SBin { get => _record?.SBin ?? ""; set => EditableIdentity().SBin = value; }
    public string HBin { get => _record?.HBin ?? ""; set => EditableIdentity().HBin = value; }
    public int? X { get => _record?.X; set => EditableIdentity().X = value; }
    public int? Y { get => _record?.Y; set => EditableIdentity().Y = value; }
    public double Value { get; set; }

    public double SiteSort => NumericText.SortKey(Site);
    public double SBinSort => NumericText.SortKey(SBin);
    public double HBinSort => NumericText.SortKey(HBin);
}
