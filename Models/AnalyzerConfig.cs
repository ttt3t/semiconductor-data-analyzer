namespace SemiconductorCsvAnalyzer.Models;

public sealed class AnalyzerConfig
{
    public string EncodingName { get; set; } = "UTF-8";
    public char Delimiter { get; set; } = ',';
    public bool HasQuotedFields { get; set; } = true;

    // Layout (1-based, 0 = not present)
    public int TestItemRow { get; set; } = 1;
    public int LowLimitRow { get; set; } = 2;
    public int HighLimitRow { get; set; } = 3;
    public int UnitRow { get; set; } = 4;
    public int TestNumberRow { get; set; } = 0;
    public int SBinRow { get; set; } = 0;
    public int HBinRow { get; set; } = 0;
    public int DataStartRow { get; set; } = 17;
    public int TestItemStartColumn { get; set; } = 6;

    // Identity columns (1-based, 0 = not present)
    public int SerialNumberColumn { get; set; } = 1;
    public int XColumn { get; set; } = 2;
    public int YColumn { get; set; } = 3;
    public int SiteColumn { get; set; } = 4;
    public int WaferColumn { get; set; } = 0;
    public int SBinColumn { get; set; } = 0;
    public int HBinColumn { get; set; } = 0;

    // Original disposition comes exclusively from HBIN; failed records are never filtered.
    public List<string> PassHBins { get; set; } = new() { "1" };
    public List<string> PartialPassHBins { get; set; } = new();
    public int ProductColumn { get; set; }
    public int ProgramColumn { get; set; }
    public int LotColumn { get; set; }
    public string Product { get; set; } = "";
    public string Program { get; set; } = "";
    public string Lot { get; set; } = "";
    public List<string> NotApplicableTokens { get; set; } = new() { "N/A", "NA", "NOT_APPLICABLE" };
    public List<string> MissingTokens { get; set; } = new() { "NOT_TESTED", "SKIP" };
    public Dictionary<string, List<string>> ProgramCompatibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Histogram
    public string BinMode { get; set; } = "Fixed";
    public int BinCount { get; set; } = 40;
    public bool ShowLowLimit { get; set; } = true;
    public bool ShowHighLimit { get; set; } = true;
    public bool ShowMean { get; set; } = true;
    public bool ShowThreeSigma { get; set; } = true;
    public bool IncludeOutOfLimitData { get; set; } = true;

    // Display
    public int DecimalPlaces { get; set; } = 6;
    public int MaxTableRows { get; set; } = 10000;

    // One-pass clipping for the robust summary columns only.
    public bool RemoveOutliers { get; set; } = true;
    public double OutlierSigma { get; set; } = 6;

    public string ConfigFileName { get; set; } = "";
    public Dictionary<string, List<string>> SBinCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
