namespace SemiconductorCsvAnalyzer.Models;

// Raw measurements retain blanks and invalid text without entering numeric statistics.
public sealed class CsvRecord
{
    public string Product { get; set; } = "";
    public string Program { get; set; } = "";
    public string Lot { get; set; } = "";
    public DateTime? StdfTestTime { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WaferId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    // Display/run serials may be reassigned; physical no-XY retest identity must not change.
    // Null means the source serial has never been replaced, while empty means it was absent.
    public string? OriginalSerialNumber { get; set; }
    public string ChipSerialNumber => OriginalSerialNumber ?? SerialNumber;
    // Keep derived UUID metadata when switching back to CP; never overwrite the source SN.
    public bool IsFtIdentity { get; set; }
    public string? RetestUuid { get; set; }
    public string UuidTestNumbers { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public string Site { get; set; } = "";
    // Legacy integrated-CSV payload only; never used for classification or filtering.
    public string Result { get; set; } = "";
    public string SBin { get; set; } = "";
    public string HBin { get; set; } = "";
    public MeasurementCollection Measurements { get; set; } = new();
    // A yield-only read retains UUID fields, but still distinguishes an empty row
    // from a row with measurements in columns outside that projection.
    internal bool HasNonblankMeasurements { get; set; }
}
