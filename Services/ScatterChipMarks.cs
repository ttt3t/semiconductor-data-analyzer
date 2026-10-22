using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Window-local chip identities; never retains a dataset or changes statistics.</summary>
public sealed class ScatterChipMarks
{
    private readonly record struct ChipKey(byte Kind, string Lot, string Wafer, int? X, int? Y, string Identity)
    {
        public static ChipKey From(CsvRecord record)
        {
            if (record.IsFtIdentity)
                return string.IsNullOrEmpty(record.RetestUuid)
                    ? new(0, "", "", null, null, record.Id)
                    : new(1, record.Lot, "", null, null, record.RetestUuid);
            if (record.X.HasValue && record.Y.HasValue)
                return new(2, record.Lot, record.WaferId, record.X, record.Y, "");
            string serial = record.ChipSerialNumber.Trim();
            return serial.Length > 0 ? new(3, record.Lot, record.WaferId, null, null, serial)
                : new(0, "", "", null, null, record.Id);
        }
    }

    private readonly HashSet<ChipKey> _chips = new();
    public int Count => _chips.Count;

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
                changed |= _chips.Add(ChipKey.From(record));
        return changed;
    }

    public bool[] CreateMask(ScatterSeries series)
    {
        if (_chips.Count == 0 || !series.HasChipIdentities) return Array.Empty<bool>();
        var mask = new bool[series.Points.Length];
        for (int i = 0; i < mask.Length; i++)
            if (series.RecordAtPoint(i) is { } record) mask[i] = _chips.Contains(ChipKey.From(record));
        return mask;
    }

    public void Clear()
    {
        _chips.Clear();
        _chips.TrimExcess();
    }
}
