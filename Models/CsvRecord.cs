using System.Text.Json.Serialization;

namespace SemiconductorCsvAnalyzer.Models;

// Raw measurements retain blanks and invalid text without entering numeric statistics.
public sealed class CsvRecord
{
    [JsonIgnore] internal Action? Changed;
    [JsonInclude] internal long StorageOrder { get; set; }
    private string _field1 = "";
    public string Product { get => _field1; set { _field1 = value; Changed?.Invoke(); } }
    private string _field2 = "";
    public string Program { get => _field2; set { _field2 = value; Changed?.Invoke(); } }
    private string _field3 = "";
    public string Lot { get => _field3; set { _field3 = value; Changed?.Invoke(); } }
    private DateTime? _field13;
    public DateTime? StdfTestTime { get => _field13; set { _field13 = value; Changed?.Invoke(); } }
    private string _field4 = Guid.NewGuid().ToString("N");
    public string Id { get => _field4; set { _field4 = value; Changed?.Invoke(); } }
    private string _field5 = "";
    public string WaferId { get => _field5; set { _field5 = value; Changed?.Invoke(); } }
    private string _field6 = "";
    public string SerialNumber { get => _field6; set { _field6 = value; Changed?.Invoke(); } }
    // Display/run serials may be reassigned; physical no-XY retest identity must not change.
    // Null means the source serial has never been replaced, while empty means it was absent.
    private string? _field14;
    public string? OriginalSerialNumber { get => _field14; set { _field14 = value; Changed?.Invoke(); } }
    public string ChipSerialNumber => OriginalSerialNumber ?? SerialNumber;
    // Keep derived UUID metadata when switching back to CP; never overwrite the source SN.
    private bool _field15;
    public bool IsFtIdentity { get => _field15; set { _field15 = value; Changed?.Invoke(); } }
    private string? _field16;
    public string? RetestUuid { get => _field16; set { _field16 = value; Changed?.Invoke(); } }
    private string _field7 = "";
    public string UuidTestNumbers { get => _field7; set { _field7 = value; Changed?.Invoke(); } }
    private int? _field17;
    public int? X { get => _field17; set { _field17 = value; Changed?.Invoke(); } }
    private int? _field18;
    public int? Y { get => _field18; set { _field18 = value; Changed?.Invoke(); } }
    private string _field8 = "";
    public string Site { get => _field8; set { _field8 = value; Changed?.Invoke(); } }
    // Legacy integrated-CSV payload only; never used for classification or filtering.
    private string _field9 = "";
    public string Result { get => _field9; set { _field9 = value; Changed?.Invoke(); } }
    private string _field10 = "";
    public string SBin { get => _field10; set { _field10 = value; Changed?.Invoke(); } }
    private string _field11 = "";
    public string HBin { get => _field11; set { _field11 = value; Changed?.Invoke(); } }
    private MeasurementCollection _field12 = new();
    [JsonIgnore]
    public MeasurementCollection Measurements { get => _field12; set { _field12 = value; Changed?.Invoke(); } }
    // A yield-only read retains UUID fields, but still distinguishes an empty row
    // from a row with measurements in columns outside that projection.
    private bool _hasNonblank;
    [JsonInclude] internal bool HasNonblankMeasurements { get => _hasNonblank; set { _hasNonblank = value; Changed?.Invoke(); } }
}
