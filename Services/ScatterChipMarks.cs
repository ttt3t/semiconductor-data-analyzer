using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Window-local acquisition record identities shared across tests, excluding other retests.</summary>
public sealed class ScatterChipMarks
{
    private readonly HashSet<string> _records = new(StringComparer.Ordinal);
    public int Count => _records.Count;

    public bool AddRectangle(ScatterSeries series, double minX, double maxX, double minY, double maxY)
    {
        if (!series.HasChipIdentities || !double.IsFinite(minX) || !double.IsFinite(maxX) ||
            !double.IsFinite(minY) || !double.IsFinite(maxY)) return false;
        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minY > maxY) (minY, maxY) = (maxY, minY);
        bool changed = false;
        var points = series.Points;
        for (int i = ScatterSeries.LowerBound(points, minX); i < points.Length && points[i].Index <= maxX; i++)
            if (points[i].Value >= minY && points[i].Value <= maxY && series.RecordAtPoint(i) is { } record)
                changed |= _records.Add(record.Id);
        return changed;
    }

    public bool[] CreateMask(ScatterSeries series)
    {
        if (_records.Count == 0 || !series.HasChipIdentities) return Array.Empty<bool>();
        var mask = new bool[series.Points.Length];
        for (int i = 0; i < mask.Length; i++)
            if (series.RecordAtPoint(i) is { } record) mask[i] = _records.Contains(record.Id);
        return mask;
    }

    public void Clear()
    {
        _records.Clear();
    }
}
