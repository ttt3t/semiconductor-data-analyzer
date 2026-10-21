using System.IO;
using Microsoft.Data.Sqlite;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public readonly record struct ScatterPoint(int Index, double Value, bool OutOfSpec)
{
    // Fits the existing struct padding on x64; no per-point identity objects.
    public int SourceIndex { get; init; }
}
public readonly record struct ScatterGroup(string Name, int StartIndex, int EndIndex);

/// <summary>Every finite measurement. No sampled or aggregated levels.</summary>
public sealed class ScatterSeries
{
    public static ScatterSeries Empty { get; } = new();
    public PagedVector<ScatterPoint> Points { get; private init; } = new();
    public int SampleCount { get; private init; }
    public double Minimum { get; private init; }
    public double Maximum { get; private init; }
    public ScatterOrder Order { get; private init; } = ScatterOrder.SerialNumber;
    public ScatterGroup[] Groups { get; private init; } = Array.Empty<ScatterGroup>();
    private IReadOnlyList<TestValue>? _identityValues;
    internal bool HasChipIdentities => _identityValues != null;
    internal IDisposable? AcquireLease() => _identityValues is TestValueColumn col ? col.AcquireLease() : _identityValues is IndexedTestValues view ? view.AcquireLease() : null;
    internal CsvRecord? RecordAtPoint(int index)
    {
        int row = Points[index].SourceIndex;
        return _identityValues switch
        {
            TestValueColumn column => column.GetRecordAt(row),
            IndexedTestValues selected => selected.GetRecordAt(row),
            null => null,
            _ => _identityValues[row].Record
        };
    }

    public ScatterSeries Scale(double factor) => factor == 1 ? this : new ScatterSeries
    {
        Points = PagedVector<ScatterPoint>.From(Points.Select(p => p with { Value = p.Value * factor })),
        SampleCount = SampleCount, Minimum = Minimum * factor, Maximum = Maximum * factor,
        Order = Order, Groups = Groups, _identityValues = _identityValues
    };

    private readonly record struct SortKey(int SourceIndex, int Wafer, double SiteNumber, string Site,
        double SerialNumber, string Serial);

    public static ScatterSeries BuildSorted(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        ScatterOrder order, CancellationToken token = default, IReadOnlyList<string>? waferOrder = null, bool trackChips = false)
    {
        token.ThrowIfCancellationRequested();
        if (values.Count > 2048) return BuildDiskSorted(values, lsl, usl, order, token, waferOrder, trackChips);
        var keys = new SortKey[values.Count];
        var wafers = new Dictionary<string, int>(StringComparer.Ordinal);
        if (waferOrder != null)
            foreach (string wafer in waferOrder) wafers.TryAdd(wafer, wafers.Count);
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var value = values[i];
            if (!wafers.TryGetValue(value.WaferId, out int wafer))
                wafers[value.WaferId] = wafer = wafers.Count;
            string site = string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site;
            keys[i] = new(i, wafer, NumericText.SortKey(site), site,
                NumericText.SortKey(value.SerialNumber), value.SerialNumber);
        }
        int comparisons = 0;
        try
        {
            Array.Sort(keys, (a, b) =>
            {
                if ((++comparisons & 4095) == 0) token.ThrowIfCancellationRequested();
                int result = 0;
                if (order == ScatterOrder.Site)
                {
                    result = a.SiteNumber.CompareTo(b.SiteNumber);
                    if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.Site, b.Site);
                    if (result == 0) result = StringComparer.Ordinal.Compare(a.Site, b.Site);
                }
                else if (order == ScatterOrder.Wafer) result = a.Wafer.CompareTo(b.Wafer);
                if (result == 0) result = a.SerialNumber.CompareTo(b.SerialNumber);
                if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.Serial, b.Serial);
                // Stable ties retain acquisition order, including repeated test records.
                return result != 0 ? result : a.SourceIndex.CompareTo(b.SourceIndex);
            });
        }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
        token.ThrowIfCancellationRequested();
        var indices = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++) indices[i] = keys[i].SourceIndex;
        var series = BuildCore(values, lsl, usl, token, indices, trackChips);
        if (order == ScatterOrder.SerialNumber) return series;
        if (series.Points.Length == 0) return new ScatterSeries { Order = order };

        // Only one small descriptor per contiguous group; measurement points retain their compact layout.
        var groups = new List<ScatterGroup>();
        int start = 0;
        for (int i = 1; i <= keys.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (i < keys.Length && (order == ScatterOrder.Site
                ? keys[i].Site == keys[start].Site : keys[i].Wafer == keys[start].Wafer)) continue;
            string name = order == ScatterOrder.Site ? keys[start].Site : values[keys[start].SourceIndex].WaferId;
            groups.Add(new(string.IsNullOrWhiteSpace(name) ? "(空)" : name, start + 1, i));
            start = i;
        }
        token.ThrowIfCancellationRequested();
        return new ScatterSeries { Points = series.Points, SampleCount = series.SampleCount,
            Minimum = series.Minimum, Maximum = series.Maximum, Order = order, Groups = groups.ToArray(),
            _identityValues = series._identityValues };
    }

    public static ScatterSeries Build(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        CancellationToken cancellationToken = default, bool trackChips = false)
        => BuildCore(values, lsl, usl, cancellationToken, null, trackChips);

    private static ScatterSeries BuildCore(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        CancellationToken cancellationToken, int[]? indices, bool trackChips)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var points = new PagedVector<ScatterPoint>();
        int count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = indices == null ? i : indices[i];
            double value = values[sourceIndex].Value;
            if (!double.IsFinite(value)) continue;
            points.Add(new(i + 1, value, (lsl.HasValue && value < lsl) || (usl.HasValue && value > usl))
                { SourceIndex = sourceIndex }); count++;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        if (count == 0) return Empty;
        points.Seal();
        return new ScatterSeries { Points = points, SampleCount = values.Count, Minimum = min, Maximum = max,
            _identityValues = trackChips ? values : null };
    }

    private static ScatterSeries BuildDiskSorted(IReadOnlyList<TestValue> values, double? lsl, double? usl,
        ScatterOrder order, CancellationToken token, IReadOnlyList<string>? waferOrder, bool trackChips)
    {
        string path = DiskStorage.NewPath(".point-order");
        try
        {
            using var database = DiskStorage.OpenDatabase(path);
            using (var create = database.CreateCommand()) { create.CommandText = "CREATE TABLE points(source INTEGER,wafer INTEGER,wafername TEXT,sitenum REAL,site TEXT,snnum REAL,sn TEXT,value REAL)"; create.ExecuteNonQuery(); }
            var wafers = new Dictionary<string, int>(StringComparer.Ordinal);
            if (waferOrder != null) foreach (string wafer in waferOrder) wafers.TryAdd(wafer, wafers.Count);
            using (var transaction = database.BeginTransaction())
            using (var insert = database.CreateCommand())
            {
                insert.CommandText = "INSERT INTO points VALUES($source,$wafer,$wafername,$sitenum,$site,$snnum,$sn,$value)";
                foreach (string parameter in new[] { "source", "wafer", "wafername", "sitenum", "site", "snnum", "sn", "value" }) insert.Parameters.Add(new SqliteParameter("$" + parameter, null));
                for (int i = 0; i < values.Count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); var value = values[i];
                    if (!wafers.TryGetValue(value.WaferId, out int wafer)) wafers[value.WaferId] = wafer = wafers.Count;
                    string site = string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site;
                    insert.Parameters[0].Value = i; insert.Parameters[1].Value = wafer; insert.Parameters[2].Value = value.WaferId;
                    insert.Parameters[3].Value = NumericText.SortKey(site); insert.Parameters[4].Value = site;
                    insert.Parameters[5].Value = NumericText.SortKey(value.SerialNumber); insert.Parameters[6].Value = value.SerialNumber;
                    insert.Parameters[7].Value = double.IsFinite(value.Value) ? value.Value : DBNull.Value; insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            var points = new PagedVector<ScatterPoint>(); var groups = new List<ScatterGroup>();
            using var read = database.CreateCommand();
            read.CommandText = "SELECT source,wafer,wafername,site,value FROM points ORDER BY " +
                (order == ScatterOrder.Site ? "sitenum,site COLLATE ORDINAL_IGNORE_CASE,site COLLATE ORDINAL," : order == ScatterOrder.Wafer ? "wafer," : "") +
                "snnum,sn COLLATE ORDINAL_IGNORE_CASE,source";
            using var reader = read.ExecuteReader();
            int index = 0, start = 1; string? group = null; string name = "";
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            while (reader.Read())
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                string next = order == ScatterOrder.Site ? reader.GetString(3) : reader.GetInt32(1).ToString();
                if (group != null && next != group && order != ScatterOrder.SerialNumber) { groups.Add(new(name, start, index)); start = index + 1; }
                group = next; name = order == ScatterOrder.Site ? reader.GetString(3) : reader.GetString(2); index++;
                if (reader.IsDBNull(4)) continue;
                double value = reader.GetDouble(4);
                points.Add(new(index, value, lsl.HasValue && value < lsl || usl.HasValue && value > usl) { SourceIndex = reader.GetInt32(0) });
                minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
            }
            if (order != ScatterOrder.SerialNumber && index > 0) groups.Add(new(string.IsNullOrWhiteSpace(name) ? "(空)" : name, start, index));
            points.Seal();
            return new ScatterSeries { Points = points, SampleCount = values.Count, Minimum = points.Count == 0 ? 0 : minimum,
                Maximum = points.Count == 0 ? 0 : maximum, Order = order, Groups = points.Count == 0 ? Array.Empty<ScatterGroup>() : groups.ToArray(), _identityValues = trackChips ? values : null };
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    public static int LowerBound(PagedVector<ScatterPoint> points, double index)
    {
        int lo = 0, hi = points.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (points[mid].Index < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public static int LowerBoundGroup(ScatterGroup[] groups, double index)
    {
        int lo = 0, hi = groups.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (groups[mid].EndIndex + 0.5 < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
