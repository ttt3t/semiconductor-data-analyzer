using System.IO;
using System.Text.Json.Serialization;

namespace SemiconductorCsvAnalyzer.Models;

public sealed class ProductCatalog
{
    public int Version { get; set; } = 1;
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public List<ManagedProduct> Products { get; set; } = new();
}

public sealed class ManagedProduct
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ConfigText { get; set; } = "";
    public string UuidTestNumbers { get; set; } = "";
    public bool IncludePartialPass { get; set; }
    public bool PreferGoodRetest { get; set; }
    public RetestMode RetestMode { get; set; } = RetestMode.Last;
    public List<ProductTestStage> Flow { get; set; } = new();
    public List<ManagedDataFile> Files { get; set; } = new();
    public ManagedFilenameRule FilenameRule { get; set; } = new();
    public ManagementSnapshot? Snapshot { get; set; }
    [JsonIgnore] public string FlowDescription => string.Join(" → ", Flow.Select(s => s.Label));
    [JsonIgnore] public string FileSummary => $"{Files.Count} 条登记 · {Flow.Count} 个测试步骤";
}

public sealed class ProductTestStage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "CP";
    public string Name { get; set; } = "常温";
    public double Temperature { get; set; } = 25;
    public bool Required { get; set; } = true;
    [JsonIgnore] public string Label => Id.StartsWith("COMBINED:") ? Kind + " · 综合良率（物理分 Bin）" : $"{Kind} · {Name} · {Temperature:g}°C";
}

public sealed class ManagedDataFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public string StageId { get; set; } = "";
    public string Lot { get; set; } = "";
    public string WaferOverride { get; set; } = "";
    public string ConfigText { get; set; } = "";
    public StdfImportMode StdfMode { get; set; }
    public DateTime RegisteredUtc { get; set; } = DateTime.UtcNow;
    public DateTime? TestTime { get; set; }
    public bool UseStdfTestTime { get; set; }
    public DateTime? DetectedStdfTestTime { get; set; }
    [JsonIgnore] public string TestTimeSource => UseStdfTestTime ? "STDF 原始时间" : "登记时间";
    [JsonIgnore] public string TestTimeText
    {
        get => TestTime?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        set => TestTime = Services.ManagedFilenameService.ParseTime(value);
    }
    public bool UseSpecifiedYield { get; set; }
    public string SpecifiedKind { get; set; } = "CP";
    public int? SpecifiedTotal { get; set; }
    public int? SpecifiedGood { get; set; }
    public double? SpecifiedPercent { get; set; }
    public bool SpecifiedIncludePartial { get; set; }
    public string SpecifiedOrigin { get; set; } = "物理分 Bin";
    [JsonIgnore] public string FileName => FilePath.Length == 0 ? "指定良率（无原文件）" : Path.GetFileName(FilePath);
}

public sealed class ManagementSnapshot
{
    public DateTime CalculatedUtc { get; set; } = DateTime.UtcNow;
    public string DefinitionSignature { get; set; } = "";
    public List<ManagedYieldRow> Yields { get; set; } = new();
    public List<ManagedCategoryCount> Categories { get; set; } = new();
    // Compact monthly contributions after whole-population retest/temperature matching.
    public List<ManagedYieldRow> MonthlyYields { get; set; } = new();
    public List<ManagedCategoryCount> MonthlyCategories { get; set; } = new();
    public List<ManagedFileResult> Files { get; set; } = new();
    [JsonIgnore] public int FailedFiles => Files.Count(f => f.Error.Length > 0);
}

public sealed class ManagedYieldRow
{
    // Registration IDs preserve source associations across wafer/lot/time aggregation.
    public HashSet<string> SourceFileIds { get; set; } = new(StringComparer.Ordinal);
    public string Period { get; set; } = "";
    [JsonIgnore] public string PeriodLabel => Period.Length == 0 ? "未指定时间" : Period;
    public string Kind { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Wafer { get; set; } = "";
    public string StageId { get; set; } = "";
    public string StageName { get; set; } = "";
    public bool IsCombined { get; set; }
    public int Total { get; set; }
    public int Pass { get; set; }
    public int PartialPass { get; set; }
    public int Good { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int MissingTemperature { get; set; }
    public int MissingIdentity { get; set; }
    public string Source { get; set; } = "自动计算";
    public string Note { get; set; } = "";
    public bool HasSampleCount { get; set; } = true;
    public bool HasDispositionCounts { get; set; } = true;
    public bool CanCalculateYield { get; set; } = true;
    public double? SpecifiedYield { get; set; }
    [JsonIgnore] public double? Yield => !CanCalculateYield ? null : SpecifiedYield ?? (Total == 0 ? null : Good * 100.0 / Total);
    [JsonIgnore] public string YieldText => !CanCalculateYield ? "无法计算" : Yield.HasValue ? Yield.Value.ToString("F2") + "%" : "–";
    [JsonIgnore] public string TotalText => HasSampleCount ? Total.ToString("N0") : "–";
    [JsonIgnore] public string GoodText => HasSampleCount ? Good.ToString("N0") : "–";
    [JsonIgnore] public string FailedText => HasSampleCount ? Failed.ToString("N0") : "–";
    [JsonIgnore] public string PassText => HasDispositionCounts ? Pass.ToString("N0") : "–";
    [JsonIgnore] public string PartialPassText => HasDispositionCounts ? PartialPass.ToString("N0") : "–";
    [JsonIgnore] public string AxisLabel => Kind == "FT" ? Lot + " / FT" : Lot + " / " + Wafer;
}

public sealed class ManagedCategoryCount
{
    public string Period { get; set; } = "";
    [JsonIgnore] public string PeriodLabel => Period.Length == 0 ? "未指定时间" : Period;
    public string Kind { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Wafer { get; set; } = "";
    public string StageId { get; set; } = "";
    public string Category { get; set; } = "";
    public int Count { get; set; }
    public int Total { get; set; }
    [JsonIgnore] public double? Percent => Total == 0 ? null : Count * 100.0 / Total;
    [JsonIgnore] public string AxisLabel => Kind == "FT" ? Lot + " / FT" : Lot + " / " + Wafer;
}

public enum ManagementPeriod { Wafer, Month, Year, Lot }

public sealed class ManagedFilenameRule
{
    public bool ExtractWafer { get; set; }
    public int WaferStart { get; set; } = 1;
    public int WaferLength { get; set; } = 2;
    public bool ExtractTime { get; set; } = true;
    public int TimeStart { get; set; } = 1;
    public int TimeLength { get; set; } // Zero: recognize standard dates anywhere in the stem.
    public string TimeFormat { get; set; } = "";
}

public sealed class ManagedFileResult
{
    public string FileId { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public int Records { get; set; }
    public int MissingIdentity { get; set; }
    public string Error { get; set; } = "";
}
