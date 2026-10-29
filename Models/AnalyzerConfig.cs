namespace SemiconductorCsvAnalyzer.Models;

public sealed class AnalyzerConfig
{
    public string EncodingName { get; set; } = "UTF-8";
    public char Delimiter { get; set; } = ',';
    public bool HasQuotedFields { get; set; } = true;

    // Layout (1-based, 0 = not present)
    public int TestItemRow { get; set; } = 1;
    // Optional hierarchical name rows; an empty list retains the legacy TestItemRow.
    public List<int> TestItemRows { get; set; } = new();
    public List<int> TestItemColumns { get; set; } = new();
    public bool SkipRepeatedHeaders { get; set; } = true;
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

    public TestAnalysisMode TestMode { get; set; } = TestAnalysisMode.CP;
    public List<string> UuidTestNumbers { get; set; } = new();

    // Original disposition comes exclusively from HBIN; failed records are never filtered.
    public List<string> PassHBins { get; set; } = new() { "1" };
    public List<string> PartialPassHBins { get; set; } = new();
    public bool PreferGoodRetest { get; set; }
    public bool GoodRetestIncludePartialPass { get; set; }
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

    // Scatter references are independent of the histogram. Legacy files keep their existing appearance.
    public bool ScatterShowLowLimit { get; set; } = true;
    public bool ScatterShowHighLimit { get; set; } = true;
    public bool ScatterShowMean { get; set; }
    public bool ScatterShowThreeSigma { get; set; }

    // Display
    public int DecimalPlaces { get; set; } = 6;
    public int MaxTableRows { get; set; } = 10000;

    // One-pass clipping for the robust summary columns only.
    public bool RemoveOutliers { get; set; } = true;
    public double OutlierSigma { get; set; } = 6;

    public string ConfigFileName { get; set; } = "";
    public Dictionary<string, List<string>> SBinCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SBinDescriptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
