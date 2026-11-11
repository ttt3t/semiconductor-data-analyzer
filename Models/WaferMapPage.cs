using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Models;

public sealed record WaferMapPage(string WaferId, IList<WaferDie> Dies, HeatmapScale? HeatmapScale = null,
    string Unit = "", bool AllowHeatmap = false, int? PassCount = null, string YieldBasis = "",
    string Summary = "", string SummaryDetails = "");
