using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class HistogramBin
{
    public double From { get; set; }
    public double To { get; set; }
    public int Count { get; set; }
    public bool IsOutOfSpec { get; set; }
}

public static class HistogramService
{
    public static List<HistogramBin> CreateFixedBins(
        IReadOnlyList<TestValue> values,
        int binCount,
        double? lsl,
        double? usl,
        bool includeOutOfLimit)
    {
        int n = values.Count;
        if (n == 0 || binCount < 1)
            return new List<HistogramBin>();

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            double v = values[i].Value;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (!includeOutOfLimit)
        {
            if (lsl.HasValue) min = Math.Max(min, lsl.Value);
            if (usl.HasValue) max = Math.Min(max, usl.Value);
        }

        if (Math.Abs(max - min) < 1e-15)
        {
            return new List<HistogramBin>
            {
                new() { From = min, To = min + 1e-9, Count = n }
            };
        }

        double width = (max - min) / binCount;
        var bins = new List<HistogramBin>(binCount);

        for (int i = 0; i < binCount; i++)
        {
            double from = min + i * width;
            double to = (i == binCount - 1) ? max : from + width;
            bool isOut = (lsl.HasValue && to <= lsl.Value) || (usl.HasValue && from >= usl.Value);
            bins.Add(new HistogramBin { From = from, To = to, IsOutOfSpec = isOut });
        }

        for (int i = 0; i < n; i++)
        {
            double v = values[i].Value;
            if (!includeOutOfLimit)
            {
                if (lsl.HasValue && v < lsl.Value) continue;
                if (usl.HasValue && v > usl.Value) continue;
            }

            int idx = (int)((v - min) / width);
            if (idx < 0) idx = 0;
            if (idx >= binCount) idx = binCount - 1;
            bins[idx].Count++;
        }

        return bins;
    }

    public static List<HistogramBin> CreateSpecZoneBins(
        IReadOnlyList<TestValue> values,
        double lsl,
        double usl,
        bool includeOutOfLimit)
    {
        int n = values.Count;
        if (n == 0 || usl <= lsl)
            return new List<HistogramBin>();

        const int leftBins = 40;
        const int midBins = 70;
        const int rightBins = 40;
        int totalBins = leftBins + midBins + rightBins;

        double width = (usl - lsl) / midBins;
        double leftStart = lsl - leftBins * width;

        var bins = new List<HistogramBin>(totalBins);

        for (int i = 0; i < leftBins; i++)
        {
            double from = leftStart + i * width;
            bins.Add(new HistogramBin { From = from, To = from + width, IsOutOfSpec = true });
        }

        for (int i = 0; i < midBins; i++)
        {
            double from = lsl + i * width;
            bins.Add(new HistogramBin
            {
                From = from,
                To = (i == midBins - 1) ? usl : from + width,
                IsOutOfSpec = false
            });
        }

        for (int i = 0; i < rightBins; i++)
        {
            double from = usl + i * width;
            bins.Add(new HistogramBin { From = from, To = from + width, IsOutOfSpec = true });
        }

        for (int i = 0; i < n; i++)
        {
            double v = values[i].Value;
            if (!includeOutOfLimit)
            {
                if (v < lsl || v > usl) continue;
            }

            int idx;
            if (v < lsl)
            {
                idx = (int)((v - leftStart) / width);
                if (idx < 0) idx = 0;
                if (idx >= leftBins) idx = leftBins - 1;
            }
            else if (v > usl)
            {
                idx = leftBins + midBins + (int)((v - usl) / width);
                if (idx >= totalBins) idx = totalBins - 1;
            }
            else
            {
                idx = leftBins + (int)((v - lsl) / width);
                if (idx >= leftBins + midBins) idx = leftBins + midBins - 1;
            }

            bins[idx].Count++;
        }

        return bins;
    }

    public static List<HistogramBin> CreateMeanCenteredBins(
        IReadOnlyList<TestValue> values,
        double? lsl,
        double? usl,
        bool includeOutOfLimit,
        out double center,
        out double scale)
    {
        center = 0;
        scale = 0;
        int n = values.Count;
        if (n == 0)
            return new List<HistogramBin>();

        var sorted = new List<double>(n);
        for (int i = 0; i < n; i++)
            sorted.Add(values[i].Value);
        sorted.Sort();

        double median = Percentile(sorted, 0.5);
        double q1 = Percentile(sorted, 0.25);
        double q3 = Percentile(sorted, 0.75);
        double iqr = q3 - q1;
        double sigma = iqr / 1.349;

        if (sigma < 1e-15)
        {
            center = median;
            scale = 0;
            return new List<HistogramBin>
            {
                new() { From = median - 1e-9, To = median + 1e-9, Count = n }
            };
        }

        center = median;
        scale = sigma;

        const int sideBins = 40;
        int totalBins = sideBins * 2;
        double width = sigma / 5.0;
        double leftStart = median - sideBins * width;

        var bins = new List<HistogramBin>(totalBins);
        for (int i = 0; i < totalBins; i++)
        {
            double from = leftStart + i * width;
            double to = from + width;
            bool isOut = (lsl.HasValue && to <= lsl.Value) || (usl.HasValue && from >= usl.Value);
            bins.Add(new HistogramBin { From = from, To = to, IsOutOfSpec = isOut });
        }

        for (int i = 0; i < n; i++)
        {
            double v = values[i].Value;
            if (!includeOutOfLimit)
            {
                if (lsl.HasValue && v < lsl.Value) continue;
                if (usl.HasValue && v > usl.Value) continue;
            }

            int idx = (int)((v - leftStart) / width);
            if (idx < 0) idx = 0;
            if (idx >= totalBins) idx = totalBins - 1;
            bins[idx].Count++;
        }

        return bins;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        double idx = p * (sorted.Count - 1);
        int i = (int)Math.Floor(idx);
        double frac = idx - i;

        if (i + 1 < sorted.Count)
            return sorted[i] * (1 - frac) + sorted[i + 1] * frac;

        return sorted[i];
    }
}
