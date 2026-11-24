using System.Globalization;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class ProductYieldService
{
    private static void AddAnalytics(ManagedProduct product, ManagementSnapshot snapshot,
        Dictionary<DeviceKey, Dictionary<string, Observation>> devices, CancellationToken token)
    {
        var config = ConfigLoader.Parse(product.ConfigText);
        bool Fail(Observation o) => o.HBin.Length > 0 && !HBinClassifier.IsGood(o.Disposition, product.IncludePartialPass);
        static (double X, double Y)? XY(DeviceKey key)
        {
            if (key.Kind != "CP" || !key.Identity.StartsWith("XY:", StringComparison.Ordinal)) return null;
            var text = key.Identity.AsSpan(3); int comma = text.IndexOf(',');
            return comma > 0 && int.TryParse(text[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(text[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ? (x, y) : null;
        }
        foreach (var scope in devices.GroupBy(d => (d.Key.Kind, d.Key.Lot, d.Key.Wafer)))
        {
            token.ThrowIfCancellationRequested();
            var population = scope.ToArray();
            var coordinates = scope.Key.Kind == "CP" ? population.Select(d => XY(d.Key)).Where(p => p.HasValue).Select(p => p!.Value).ToArray() : Array.Empty<(double X, double Y)>();
            double cx = coordinates.Length == 0 ? 0 : (coordinates.Min(p => p.X) + coordinates.Max(p => p.X)) / 2;
            double cy = coordinates.Length == 0 ? 0 : (coordinates.Min(p => p.Y) + coordinates.Max(p => p.Y)) / 2;
            double radius = coordinates.Select(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))).DefaultIfEmpty(0).Max();
            foreach (var stage in product.Flow.Where(s => s.Kind == scope.Key.Kind))
            {
                token.ThrowIfCancellationRequested();
                var selected = population.Where(d => d.Value.ContainsKey(stage.Id)).ToArray();
                var failures = selected.Select(d => d.Value[stage.Id]).Where(Fail).ToArray();
                foreach (string type in new[] { "HBIN", "SBIN" })
                    foreach (var bin in failures.GroupBy(o => (Period: Month(o.TestTime), Bin: BinStatisticsService.NormalizeBin(type == "HBIN" ? o.HBin : o.SBin))))
                        snapshot.FailBins.Add(new()
                        {
                            Kind = scope.Key.Kind, Lot = scope.Key.Lot, Wafer = scope.Key.Wafer, StageId = stage.Id,
                            Period = bin.Key.Period, BinType = type, Bin = bin.Key.Bin.Length == 0 ? "未记录" : bin.Key.Bin,
                            Description = type == "SBIN" ? config.SBinDescriptions.GetValueOrDefault(bin.Key.Bin, "") : "", Count = bin.Count()
                        });
                if (radius <= 0 || !double.IsFinite(radius)) continue;
                var rings = Enumerable.Range(0, 4).Select(region => new ManagedRadialCount
                { Lot = scope.Key.Lot, Wafer = scope.Key.Wafer, StageId = stage.Id, Region = region, CenterX = cx, CenterY = cy, Radius = radius }).ToArray();
                foreach (var device in selected)
                {
                    if (XY(device.Key) is not { } point) continue;
                    double normalized = Math.Sqrt((point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy)) / radius;
                    int region = normalized <= .4 ? 0 : normalized <= .7 ? 1 : normalized <= .9 ? 2 : 3;
                    rings[region].Total++;
                    if (HBinClassifier.IsGood(device.Value[stage.Id].Disposition, product.IncludePartialPass)) rings[region].Good++;
                }
                snapshot.Radial.AddRange(rings);
            }
        }
    }

    private readonly record struct RankedBin(string Code, decimal? Number)
    {
        public bool Precedes(RankedBin? other) => other is not { } candidate ||
            (Number is decimal number ? candidate.Number is not decimal otherNumber || number < otherNumber
                : candidate.Number == null && StringComparer.Ordinal.Compare(Code, candidate.Code) < 0);
    }

    private static void CountCombinedBin(Dictionary<(string Period, string BinType, string Bin, bool IsFailure), int> counts,
        Observation[] observations, bool complete, string period, bool soft, bool includePartial,
        Dictionary<string, RankedBin> binCodes)
    {
        RankedBin? fail = null, partial = null;
        foreach (var observation in observations)
        {
            string raw = soft ? observation.SBin : observation.HBin;
            if (raw.Length == 0 || observation.HBin.Length == 0)
            {
                complete = false;
                continue;
            }
            if (!binCodes.TryGetValue(raw, out var bin))
            {
                string code = BinStatisticsService.NormalizeBin(raw);
                binCodes.Add(raw, bin = new(code, decimal.TryParse(code, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal number) ? number : null));
            }
            if (bin.Code == "1") continue;
            // PartialPass is configured on the product's HBIN, including when retaining an SBIN number.
            if (observation.Disposition == OriginalDisposition.PartialPass)
            {
                if (bin.Precedes(partial)) partial = bin;
            }
            else if (bin.Precedes(fail)) fail = bin;
        }
        // A known failure remains attributable even when another mandatory test is missing.
        string combined = fail?.Code ?? (complete ? partial?.Code ?? "1" : "");
        bool isFailure = fail.HasValue || complete && partial.HasValue && !includePartial;
        var key = (period, soft ? "SBIN" : "HBIN", combined, isFailure);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
}
