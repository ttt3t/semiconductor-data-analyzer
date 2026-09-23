using System.ComponentModel;

namespace SemiconductorCsvAnalyzer.Models;

public sealed class TestItemSummary : INotifyPropertyChanged
{
    public const string CombinedSite = "ALL";

    public TestItemData Data { get; set; } = null!;

    public string TestNumber => Data.TestNumber;
    public string DisplayName => Data.DisplayName;
    public string SBin => Data.SBin;
    public string HBin => Data.HBin;
    public string Unit => Data.Unit;

    public string Site { get; set; } = "";
    public string LslText { get; set; } = "-";
    public string UslText { get; set; } = "-";

    public long TestNumberValue { get; set; }
    public int Count { get; set; }
    public double Mean { get; set; }
    private double? _runValue;
    // Only this cell changes when browsing a Run. Statistical bindings stay OneTime.
    public double? RunValue
    {
        get => _runValue;
        set
        {
            if (_runValue == value) return;
            _runValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunValueFails)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool RunValueFails => RunValue is double value && double.IsFinite(value) && Data != null &&
        (Data.LowLimit is double low && value < low || Data.HighLimit is double high && value > high);
    public double? Median { get; set; }
    public double Sigma { get; set; }
    public double? RobustMean { get; set; }
    public double? RobustSigma { get; set; }
    public double? Cpk { get; set; }
    public double? Cp { get; set; }
    public double InSpecPercent { get; set; }
    public double? SiteSigma { get; set; }
    public int Eorr { get; set; }

    public double SiteSort => NumericText.SortKey(Site);
    public double SBinSort => NumericText.SortKey(SBin);
    public double HBinSort => NumericText.SortKey(HBin);
    public double LslSort => NumericText.SortKey(Data?.LowLimit);
    public double UslSort => NumericText.SortKey(Data?.HighLimit);

    public bool IsCombined => Site == CombinedSite;
}
