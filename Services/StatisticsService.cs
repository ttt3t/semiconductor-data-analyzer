using System.Buffers;
using System.Runtime.InteropServices;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class StatisticsService
{
    public static double? CalculateSiteYieldSigma(TestItemData item)
        => CalculateSiteYieldSigma(item.Values, item.LowLimit, item.HighLimit);

    public static double? CalculateSiteYieldSigma(IReadOnlyList<TestValue> values, double? lowLimit, double? highLimit)
    {
        int n = values.Count;
        if (n == 0) return null;
        var sites = new Dictionary<string, (int Count, int InSpec)>(StringComparer.Ordinal);
        if (values is TestValueColumn col)
        {
            for (int i = 0; i < n; i++)
            {
                string rawSite = col.GetRecordAt(i).Site;
                string site = string.IsNullOrWhiteSpace(rawSite) ? "(空)" : rawSite;
                double v = col.GetValueAt(i);
                bool failed = (lowLimit.HasValue && v < lowLimit.Value) ||
                    (highLimit.HasValue && v > highLimit.Value);
                ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(sites, site, out _);
                counts.Count++;
                if (!failed) counts.InSpec++;
            }
        }
        else if (values is IndexedTestValues idx)
        {
            for (int i = 0; i < n; i++)
            {
                string rawSite = idx.GetRecordAt(i).Site;
                string site = string.IsNullOrWhiteSpace(rawSite) ? "(空)" : rawSite;
                double v = idx.GetValueAt(i);
                bool failed = (lowLimit.HasValue && v < lowLimit.Value) ||
                    (highLimit.HasValue && v > highLimit.Value);
                ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(sites, site, out _);
                counts.Count++;
                if (!failed) counts.InSpec++;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                var value = values[i];
                string site = string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site;
                bool failed = (lowLimit.HasValue && value.Value < lowLimit.Value) ||
                    (highLimit.HasValue && value.Value > highLimit.Value);
                ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(sites, site, out _);
                counts.Count++;
                if (!failed) counts.InSpec++;
            }
        }
        if (sites.Count == 0) return null;
        double sum = 0;
        foreach (var s in sites.Values)
            sum += 100.0 * s.InSpec / s.Count;
        double mean = sum / sites.Count;
        double varSum = 0;
        foreach (var s in sites.Values)
        {
            double diff = 100.0 * s.InSpec / s.Count - mean;
            varSum += diff * diff;
        }
        return Math.Sqrt(varSum / sites.Count);
    }

    public static (double? Mean, double? Sigma) CalculateRobust(
        IReadOnlyList<TestValue> values, StatisticsResult original, bool removeOutliers, double sigmaMultiplier)
    {
        if (!double.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier));
        int n = values.Count;
        if (n == 0) return (null, null);
        if (!removeOutliers) return (original.Mean, original.StdDev);
        if (!double.IsFinite(original.Mean) || !double.IsFinite(original.StdDev)) return (null, null);

        if (values is TestValueColumn col && col.TryGetContiguousValues(out var contiguous))
            return CalculateRobust(contiguous, original, removeOutliers, sigmaMultiplier);

        double[] rented = ArrayPool<double>.Shared.Rent(n);
        try
        {
            CopyValues(values, rented.AsSpan(0, n));
            return CalculateRobust(rented.AsSpan(0, n), original, removeOutliers, sigmaMultiplier);
        }
        finally { ArrayPool<double>.Shared.Return(rented); }
    }

    internal static (double? Mean, double? Sigma) CalculateRobust(
        ReadOnlySpan<double> values, StatisticsResult original, bool removeOutliers, double sigmaMultiplier)
    {
        if (!double.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier));
        if (values.IsEmpty) return (null, null);
        if (!removeOutliers) return (original.Mean, original.StdDev);
        if (!double.IsFinite(original.Mean) || !double.IsFinite(original.StdDev)) return (null, null);

        // The bounds are based on the original population, not iteratively narrowed.
        double radius = sigmaMultiplier * original.StdDev;
        double lower = original.Mean - radius, upper = original.Mean + radius;
        double mean = 0, m2 = 0;
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
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
        int n = values.Count;
        if (n == 0) return null;

        // Sort a pooled copy so sample order remains unchanged in the value table and scatter plot.
        double[] sorted = ArrayPool<double>.Shared.Rent(n);
        try
        {
            CopyValues(values, sorted.AsSpan(0, n));
            Array.Sort(sorted, 0, n);
            int middle = n / 2;
            return n % 2 == 1
                ? sorted[middle]
                : sorted[middle - 1] * 0.5 + sorted[middle] * 0.5;
        }
        finally { ArrayPool<double>.Shared.Return(sorted); }
    }

    internal static (StatisticsResult Stats, (double? Mean, double? Sigma) Robust, double? Median) CalculateSummaryMetrics(
        IReadOnlyList<TestValue> values, double? lsl, double? usl, bool removeOutliers, double sigmaMultiplier)
    {
        if (!double.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier));
        int n = values.Count;
        if (n == 0)
            return (new StatisticsResult(), (null, null), null);

        double[] buffer = ArrayPool<double>.Shared.Rent(n);
        try
        {
            var span = buffer.AsSpan(0, n);
            CopyValues(values, span);
            var stats = Calculate(span, lsl, usl);
            var robust = CalculateRobust(span, stats, removeOutliers, sigmaMultiplier);
            Array.Sort(buffer, 0, n);
            int middle = n / 2;
            double median = n % 2 == 1
                ? buffer[middle]
                : buffer[middle - 1] * 0.5 + buffer[middle] * 0.5;
            return (stats, robust, median);
        }
        finally { ArrayPool<double>.Shared.Return(buffer); }
    }

    public static StatisticsResult Calculate(IReadOnlyList<TestValue> values, double? lsl, double? usl)
    {
        int n = values.Count;
        if (n == 0)
            return new StatisticsResult();

        if (values is TestValueColumn col && col.TryGetContiguousValues(out var contiguous))
            return Calculate(contiguous, lsl, usl);

        double[] rented = ArrayPool<double>.Shared.Rent(n);
        try
        {
            CopyValues(values, rented.AsSpan(0, n));
            return Calculate(rented.AsSpan(0, n), lsl, usl);
        }
        finally { ArrayPool<double>.Shared.Return(rented); }
    }

    internal static StatisticsResult Calculate(ReadOnlySpan<double> values, double? lsl, double? usl)
    {
        int n = values.Length;
        if (n == 0)
            return new StatisticsResult();

        double sum = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int below = 0, above = 0, inSpec = 0;
        bool hasLsl = lsl.HasValue, hasUsl = usl.HasValue;
        double lslVal = lsl.GetValueOrDefault(), uslVal = usl.GetValueOrDefault();

        for (int i = 0; i < n; i++)
        {
            double x = values[i];
            sum += x;
            if (x < min) min = x;
            if (x > max) max = x;
            bool lowFail = hasLsl && x < lslVal;
            bool highFail = hasUsl && x > uslVal;
            if (lowFail) below++;
            if (highFail) above++;
            // Count each sample once, including malformed overlapping limits.
            if (!lowFail && !highFail) inSpec++;
        }

        double mean = sum / n;
        double varSum = 0;
        for (int i = 0; i < n; i++)
        {
            double d = values[i] - mean;
            varSum += d * d;
        }

        double sigma = Math.Sqrt(varSum / n);

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

        if (hasLsl && hasUsl)
        {
            result.Cp = (uslVal - lslVal) / (6 * sigma);
            double cpu = (uslVal - mean) / (3 * sigma);
            double cpl = (mean - lslVal) / (3 * sigma);
            result.Cpk = Math.Min(cpu, cpl);
            result.Cpu = cpu;
            result.Cpl = cpl;
        }
        else if (hasUsl)
        {
            result.Cpu = (uslVal - mean) / (3 * sigma);
            result.Cpk = result.Cpu;
        }
        else if (hasLsl)
        {
            result.Cpl = (mean - lslVal) / (3 * sigma);
            result.Cpk = result.Cpl;
        }

        return result;
    }

    private static void CopyValues(IReadOnlyList<TestValue> source, Span<double> destination)
    {
        if (source is TestValueColumn col)
        {
            col.CopyValuesTo(destination);
        }
        else if (source is IndexedTestValues idx)
        {
            idx.CopyValuesTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = source[i].Value;
        }
    }
}
