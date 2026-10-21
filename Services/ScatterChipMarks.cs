using System.Text.Json;
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

    private DiskKeySet? _chips;
    private DiskKeySet Chips => _chips ??= new();
    public int Count => _chips?.Count ?? 0;

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
                changed |= Chips.Add(JsonSerializer.Serialize(ChipKey.From(record)));
        return changed;
    }

    public bool[] CreateMask(ScatterSeries series)
    {
        if (Count == 0 || !series.HasChipIdentities) return Array.Empty<bool>();
        var mask = new bool[series.Points.Length];
        for (int i = 0; i < mask.Length; i++)
            if (series.RecordAtPoint(i) is { } record) mask[i] = Chips.Contains(JsonSerializer.Serialize(ChipKey.From(record)));
        return mask;
    }

    internal PagedVector<byte>? CreatePagedMask(ScatterSeries series, CancellationToken token = default)
    {
        if (Count == 0 || !series.HasChipIdentities) return null;
        var mask = new PagedVector<byte>();
        for (int i = 0; i < series.Points.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            mask.Add(series.RecordAtPoint(i) is { } record && Chips.Contains(JsonSerializer.Serialize(ChipKey.From(record))) ? (byte)1 : (byte)0);
        }
        mask.Seal(); return mask;
    }
    internal DiskKeySet SelectRectangle(ScatterSeries series, double x1, double x2, double y1, double y2, CancellationToken token)
    {
        var selection = new DiskKeySet();
        try
        {
            double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2), minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
            for (int i = ScatterSeries.LowerBound(series.Points, minX); i < series.Points.Count && series.Points[i].Index <= maxX; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var point = series.Points[i];
                if (point.Value >= minY && point.Value <= maxY && series.RecordAtPoint(i) is { } record) selection.Add(JsonSerializer.Serialize(ChipKey.From(record)));
            }
            return selection;
        }
        catch { selection.Dispose(); throw; }
    }
    internal void Merge(DiskKeySet selection) => Chips.UnionWith(selection);
    public void Clear()
    {
        _chips?.Clear();

    }
}
