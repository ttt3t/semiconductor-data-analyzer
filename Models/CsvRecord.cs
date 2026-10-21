using System.Text.Json.Serialization;

namespace SemiconductorCsvAnalyzer.Models;

// Raw measurements retain blanks and invalid text without entering numeric statistics.
public sealed class CsvRecord
{
    [JsonIgnore] internal Action? Changed;
    [JsonInclude] internal long StorageOrder { get; set; }
    private string _product = "";
    public string Product { get => _product; set { if (_product == value) return; _product = value; Changed?.Invoke(); } }
    private string _program = "";
    public string Program { get => _program; set { if (_program == value) return; _program = value; Changed?.Invoke(); } }
    private string _lot = "";
    public string Lot { get => _lot; set { if (_lot == value) return; _lot = value; Changed?.Invoke(); } }
    private DateTime? _stdfTestTime;
    public DateTime? StdfTestTime { get => _stdfTestTime; set { if (_stdfTestTime == value) return; _stdfTestTime = value; Changed?.Invoke(); } }
    private string _id = Guid.NewGuid().ToString("N");
    public string Id { get => _id; set { if (_id == value) return; _id = value; Changed?.Invoke(); } }
    private string _waferId = "";
    public string WaferId { get => _waferId; set { if (_waferId == value) return; _waferId = value; Changed?.Invoke(); } }
    private string _serialNumber = "";
    public string SerialNumber { get => _serialNumber; set { if (_serialNumber == value) return; _serialNumber = value; Changed?.Invoke(); } }
    // Display/run serials may be reassigned; physical no-XY retest identity must not change.
    // Null means the source serial has never been replaced, while empty means it was absent.
    private string? _originalSerialNumber;
    public string? OriginalSerialNumber { get => _originalSerialNumber; set { if (_originalSerialNumber == value) return; _originalSerialNumber = value; Changed?.Invoke(); } }
    public string ChipSerialNumber => OriginalSerialNumber ?? SerialNumber;
    // Keep derived UUID metadata when switching back to CP; never overwrite the source SN.
    private bool _isFtIdentity;
    public bool IsFtIdentity { get => _isFtIdentity; set { if (_isFtIdentity == value) return; _isFtIdentity = value; Changed?.Invoke(); } }
    private string? _retestUuid;
    public string? RetestUuid { get => _retestUuid; set { if (_retestUuid == value) return; _retestUuid = value; Changed?.Invoke(); } }
    private string _uuidTestNumbers = "";
    public string UuidTestNumbers { get => _uuidTestNumbers; set { if (_uuidTestNumbers == value) return; _uuidTestNumbers = value; Changed?.Invoke(); } }
    private int? _x;
    public int? X { get => _x; set { if (_x == value) return; _x = value; Changed?.Invoke(); } }
    private int? _y;
    public int? Y { get => _y; set { if (_y == value) return; _y = value; Changed?.Invoke(); } }
    private string _site = "";
    public string Site { get => _site; set { if (_site == value) return; _site = value; Changed?.Invoke(); } }
    // Legacy integrated-CSV payload only; never used for classification or filtering.
    private string _result = "";
    public string Result { get => _result; set { if (_result == value) return; _result = value; Changed?.Invoke(); } }
    private string _sBin = "";
    public string SBin { get => _sBin; set { if (_sBin == value) return; _sBin = value; Changed?.Invoke(); } }
    private string _hBin = "";
    public string HBin { get => _hBin; set { if (_hBin == value) return; _hBin = value; Changed?.Invoke(); } }
    private MeasurementCollection _measurements = new();
    [JsonIgnore]
    public MeasurementCollection Measurements { get => _measurements; set { if (_measurements == value) return; _measurements = value; Changed?.Invoke(); } }
    // A yield-only read retains UUID fields, but still distinguishes an empty row
    // from a row with measurements in columns outside that projection.
    private bool _hasNonblank;
    [JsonInclude] internal bool HasNonblankMeasurements { get => _hasNonblank; set { if (_hasNonblank == value) return; _hasNonblank = value; Changed?.Invoke(); } }
}
