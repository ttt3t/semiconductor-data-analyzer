using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class StatisticsService
{
    public static double? CalculateSiteYieldSigma(TestItemData item)
    {
        var sites = new Dictionary<string, (int Count, int InSpec)>();
        foreach (var value in item.Values)
        {
            string site = string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site;
            sites.TryGetValue(site, out var counts);
            bool failed = (item.LowLimit.HasValue && value.Value < item.LowLimit.Value) ||
                (item.HighLimit.HasValue && value.Value > item.HighLimit.Value);
            sites[site] = (counts.Count + 1, counts.InSpec + (failed ? 0 : 1));
        }
        var yields = sites.Values.Select(s => 100.0 * s.InSpec / s.Count).ToArray();
        if (yields.Length == 0) return null;
        double mean = yields.Average();
        return Math.Sqrt(yields.Sum(y => (y - mean) * (y - mean)) / yields.Length);
    }
    public static (double? Mean, double? Sigma) CalculateRobust(
        IReadOnlyList<TestValue> values, StatisticsResult original, bool removeOutliers, double sigmaMultiplier)
    {
        if (!double.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier));
        if (values.Count == 0) return (null, null);
        if (!removeOutliers) return (original.Mean, original.StdDev);
        if (!double.IsFinite(original.Mean) || !double.IsFinite(original.StdDev)) return (null, null);

        // The bounds are based on the original population, not iteratively narrowed.
        double radius = sigmaMultiplier * original.StdDev;
        double lower = original.Mean - radius, upper = original.Mean + radius;
        double mean = 0, m2 = 0;
        int count = 0;
        foreach (var sample in values)
        {
            double value = sample.Value;
            if (!double.IsFinite(value) || value < lower || value > upper) continue;
            count++;
            double delta = value - mean;
            mean += delta / count;
            m2 += delta * (value - mean);
        }
        return count == 0 ? (null, null) : (mean, Math.Sqrt(Math.Max(0, m2 / count)));
    }

    public static double? CalculateMedian(IReadOnlyList<TestValue> values)
    {
        if (values.Count == 0) return null;

        // Sort a copy so sample order remains unchanged in the value table and scatter plot.
        var sorted = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
            sorted[i] = values[i].Value;
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : sorted[middle - 1] * 0.5 + sorted[middle] * 0.5;
    }

    public static StatisticsResult Calculate(IReadOnlyList<TestValue> values, double? lsl, double? usl)
    {
        int n = values.Count;
        if (n == 0)
            return new StatisticsResult();

        double sum = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int below = 0, above = 0;

        for (int i = 0; i < n; i++)
        {
            double x = values[i].Value;
            sum += x;
            if (x < min) min = x;
            if (x > max) max = x;
            if (lsl.HasValue && x < lsl.Value) below++;
            if (usl.HasValue && x > usl.Value) above++;
        }

        double mean = sum / n;
        double varSum = 0;
        for (int i = 0; i < n; i++)
        {
            double d = values[i].Value - mean;
            varSum += d * d;
        }

        double sigma = Math.Sqrt(varSum / n);
        int inSpec = n - below - above;

        var result = new StatisticsResult
        {
            Count = n,
            Mean = mean,
            StdDev = sigma,
            Min = min,
            Max = max,
            BelowLsl = below,
            AboveUsl = above,
            InSpec = inSpec,
            InSpecPercent = 100.0 * inSpec / n
        };

        if (sigma < 1e-15)
            return result;

        if (lsl.HasValue && usl.HasValue)
        {
            result.Cp = (usl.Value - lsl.Value) / (6 * sigma);
            double cpu = (usl.Value - mean) / (3 * sigma);
            double cpl = (mean - lsl.Value) / (3 * sigma);
            result.Cpk = Math.Min(cpu, cpl);
            result.Cpu = cpu;
            result.Cpl = cpl;
        }
        else if (usl.HasValue)
        {
            result.Cpu = (usl.Value - mean) / (3 * sigma);
            result.Cpk = result.Cpu;
        }
        else if (lsl.HasValue)
        {
            result.Cpl = (mean - lsl.Value) / (3 * sigma);
            result.Cpk = result.Cpl;
        }

        return result;
    }
}
