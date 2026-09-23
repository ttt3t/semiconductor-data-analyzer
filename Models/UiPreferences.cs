namespace SemiconductorCsvAnalyzer.Models;

/// <summary>Local presentation preferences, deliberately separate from the analysis INI.</summary>
public sealed class UiPreferences
{
    public bool CompactTable { get; set; } = true;
    public bool ShowColumnFilters { get; set; } = true;
    public bool AlternateRows { get; set; } = true;
    public StdfImportMode DefaultStdfMode { get; set; } = StdfImportMode.Standard;

    public UiPreferences Clone() => (UiPreferences)MemberwiseClone();
}
