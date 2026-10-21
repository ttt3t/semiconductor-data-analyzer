using System.IO;
using System.Collections;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Records are paged from an indexed identity store; manual small fixtures retain ordinary list semantics.</summary>
public sealed class RecordList : IList<CsvRecord>, IReadOnlyList<CsvRecord>
{
    private List<CsvRecord>? _memory;
    internal RecordStore? Store { get; private set; }
    internal readonly HashSet<DiskResource> Resources = new();
    internal readonly HashSet<RawMeasurementStore> RawStores = new();
    internal string ScopeKey { get; set; } = Guid.NewGuid().ToString("N");
    public RecordList() => _memory = new();
    public RecordList(int capacity, bool disk = false)
    {
        if (disk) { Store = new RecordStore(this); Resources.Add(Store); }
        else _memory = new(capacity);
    }
    private RecordList(List<CsvRecord> source) => _memory = source;
    internal string SiteAt(int row) => Store != null ? Store.CodeAt(row, true) : this[row].Site;
    internal string WaferAt(int row) => Store != null ? Store.CodeAt(row, false) : this[row].WaferId;
    internal IEnumerable<(string Site, string Wafer)> ReadIdentityCodes()
        => Store != null ? Store.ReadIdentityCodes() : this.Select(r => (r.Site, r.WaferId));
    internal sealed record Snapshot(string Path, int Count, string[] RawPaths, string[][] Schemas);
    internal Snapshot Capture()
    {
        Flush(); return Store!.Capture();
    }
    internal static RecordList Open(Snapshot snapshot)
    {
        var result = new RecordList(); result._memory = null; result.Store = new RecordStore(result, snapshot); result.Resources.Add(result.Store); return result;
    }
    public bool IsOnDisk => Store != null;
    public int Count => _memory?.Count ?? Store!.Count;
    public bool IsReadOnly => false;
    public CsvRecord this[int index]
    {
        get => _memory != null ? _memory[index] : Store!.Get(index);
        set { if (_memory != null) _memory[index] = value; else Store!.Replace(index, value); }
    }
    public void Add(CsvRecord record)
    {
        if (_memory is { Count: >= 2048 })
        {
            var previous = _memory; _memory = null; Store = new RecordStore(this); Resources.Add(Store);
            foreach (var row in previous) Store.Add(row);
        }
        if (_memory != null) _memory.Add(record); else Store!.Add(record);
    }
    public void AddRange(IEnumerable<CsvRecord> records) { foreach (var record in records) Add(record); }
    public void Clear() { if (_memory != null) _memory.Clear(); else Store!.Clear(); }
    public int IndexOf(CsvRecord value) => Store != null ? Store.Find(value.Id) : _memory!.IndexOf(value);
    public bool Contains(CsvRecord value) => IndexOf(value) >= 0;
    public void CopyTo(CsvRecord[] array, int index) { foreach (var record in this) array[index++] = record; }
    public void Insert(int index, CsvRecord value)
    { if (_memory != null) _memory.Insert(index, value); else throw new NotSupportedException("磁盘快照只支持追加或替换。"); }
    public void RemoveAt(int index)
    { if (_memory != null) _memory.RemoveAt(index); else throw new NotSupportedException("请建立筛选视图，不修改磁盘快照。"); }
    public bool Remove(CsvRecord value) { int index = IndexOf(value); if (index < 0) return false; RemoveAt(index); return true; }
    public IEnumerator<CsvRecord> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public static implicit operator RecordList(List<CsvRecord> records) => records.Count < 2048 ? new(records) : records.ToRecordList();
    public void Flush() => Store?.Flush();
    internal void Inherit(RecordList source)
    { Resources.UnionWith(source.Resources); RawStores.UnionWith(source.RawStores); }
    internal int FindId(string id) => Store != null ? Store.Find(id) : _memory!.FindIndex(r => r.Id == id);
    internal IEnumerable<CsvRecord> OrderedByChip()
        => Store != null ? Store.Query("SELECT slot FROM records ORDER BY chip COLLATE ORDINAL,slot") : this.OrderBy(r => RecordStore.Key(DatasetService.ChipKey(r)), StringComparer.Ordinal);
    internal IEnumerable<CsvRecord> OrderedBySerial() => Store != null ? Store.Query("SELECT slot FROM records ORDER BY serialnumber,slot") : this.OrderBy(r => r.SerialNumber, StringComparer.Ordinal);

    internal sealed class RecordStore : DiskResource
    {
        private readonly string _path = DiskStorage.NewPath(".identity");
        private readonly bool _readerOnly;
        internal readonly SqliteConnection Connection;
        private readonly RecordList _owner;
        private readonly List<RawMeasurementStore> _stores = new();
        private readonly List<MeasurementSchema> _schemas = new();
        private readonly Dictionary<RawMeasurementStore, int> _storeIds = new();
        private readonly Dictionary<MeasurementSchema, int> _schemaIds = new();
        private SqliteTransaction? _transaction;
        private int _writes;
        public int Count { get; private set; }
        private const int PageRows = 64;
        private sealed class Page { public required CsvRecord[] Rows; public bool Dirty; public required int Start; }
        private sealed record Handle(int Store, int Schema, long Offset, int Length, byte[]? Presence, int Count, Dictionary<string, string>? Editable);
        public RecordStore(RecordList owner)
        {
            _owner = owner; Connection = DiskStorage.OpenDatabase(_path);
            Execute("CREATE TABLE records(slot INTEGER PRIMARY KEY,id TEXT NOT NULL UNIQUE,serialnumber TEXT,wafer TEXT,site TEXT,chip TEXT,device TEXT,json TEXT,raw TEXT,ord INTEGER,x INTEGER,y INTEGER); CREATE INDEX chip_records ON records(chip,slot); CREATE INDEX record_serial ON records(serialnumber); CREATE INDEX record_wafer_site ON records(wafer,site); CREATE INDEX record_xy ON records(wafer,x,y,slot); CREATE TABLE metrics(key TEXT PRIMARY KEY,json TEXT);");
            _transaction = Connection.BeginTransaction();
        }
        internal Snapshot Capture() => new(_path, Count, _stores.Select(s => s.StoragePath).ToArray(), _schemas.Select(s => s.Keys).ToArray());
        internal RecordStore(RecordList owner, Snapshot snapshot) : base(false)
        {
            _path = snapshot.Path; _readerOnly = true; _owner = owner; Count = snapshot.Count;
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); Connection.Open();
            using (var c = Connection.CreateCommand()) { c.CommandText = "PRAGMA cache_size=-2048; PRAGMA mmap_size=0; PRAGMA temp_store=FILE"; c.ExecuteNonQuery(); }
            Connection.CreateCollation("ORDINAL", StringComparer.Ordinal.Compare); Connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            foreach (string path in snapshot.RawPaths) { var raw = new RawMeasurementStore(path); _stores.Add(raw); owner.RawStores.Add(raw); }
            foreach (string[] schema in snapshot.Schemas) _schemas.Add(new(schema));
        }
        internal static string Key((string Wafer, int? X, int? Y, string Fallback) key) => JsonSerializer.Serialize(new object?[] { key.Wafer, key.X, key.Y, key.Fallback });
        private void Execute(string sql) { using var c = Connection.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        private string Raw(MeasurementCollection measurements)
        {
            var h = measurements.CaptureHandle();
            if (h.Store == null) return JsonSerializer.Serialize(new Handle(-1, -1, 0, 0, null, h.Count, h.Editable));
            if (!_storeIds.TryGetValue(h.Store, out int store)) { store = _stores.Count; _stores.Add(h.Store); _storeIds.Add(h.Store, store); _owner.RawStores.Add(h.Store); }
            if (!_schemaIds.TryGetValue(h.Schema!, out int schema)) { schema = _schemas.Count; _schemas.Add(h.Schema!); _schemaIds.Add(h.Schema!, schema); }
            return JsonSerializer.Serialize(new Handle(store, schema, h.Offset, h.Length, h.Presence, h.Count, null));
        }
        private readonly record struct Codes(int Site, int Wafer);
        private PagedVector<Codes>? _codes;
        private readonly List<string> _sites = new(), _wafers = new();
        internal string CodeAt(int row, bool site)
        {
            lock (DiskStorage.Sync)
            {
                if (_codes == null)
                {
                    Flush(); var codes = new PagedVector<Codes>();
                    var sites = new Dictionary<string, int>(StringComparer.Ordinal); var wafers = new Dictionary<string, int>(StringComparer.Ordinal);
                    _sites.Clear(); _wafers.Clear();
                    using var c = Connection.CreateCommand(); c.CommandText = "SELECT site,wafer FROM records ORDER BY slot";
                    using var r = c.ExecuteReader();
                    while (r.Read())
                    {
                        string s = r.GetString(0), w = r.GetString(1);
                        if (!sites.TryGetValue(s, out int si)) { sites.Add(s, si = _sites.Count); _sites.Add(s); }
                        if (!wafers.TryGetValue(w, out int wi)) { wafers.Add(w, wi = _wafers.Count); _wafers.Add(w); }
                        codes.Add(new(si, wi));
                    }
                    codes.Seal(); _codes = codes;
                }
                var code = _codes[row]; return site ? _sites[code.Site] : _wafers[code.Wafer];
            }
        }
        private void Write(int slot, CsvRecord r)
        {
            _codes?.Dispose(); _codes = null;
            using var c = Connection.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO records VALUES($slot,$id,$sn,$wafer,$site,$chip,$device,$json,$raw,$ord,$x,$y)";
            c.Parameters.AddWithValue("$slot", slot); c.Parameters.AddWithValue("$id", r.Id); c.Parameters.AddWithValue("$sn", r.SerialNumber.Trim());
            c.Parameters.AddWithValue("$wafer", r.WaferId); c.Parameters.AddWithValue("$site", r.Site);
            c.Parameters.AddWithValue("$chip", Key(DatasetService.ChipKey(r)));
            bool blank = !r.X.HasValue && !r.Y.HasValue && string.IsNullOrWhiteSpace(r.ChipSerialNumber) && string.IsNullOrWhiteSpace(r.Site) && string.IsNullOrWhiteSpace(r.SBin) && string.IsNullOrWhiteSpace(r.HBin) && !r.HasNonblankMeasurements;
            c.Parameters.AddWithValue("$device", blank ? DBNull.Value : Key(r.IsFtIdentity ? DatasetService.ChipKey(r) : (r.WaferId, r.X, r.Y, r.X.HasValue && r.Y.HasValue ? "" : r.Id)));
            c.Parameters.AddWithValue("$ord", r.StorageOrder); c.Parameters.AddWithValue("$x", (object?)r.X ?? DBNull.Value); c.Parameters.AddWithValue("$y", (object?)r.Y ?? DBNull.Value);
            c.Parameters.AddWithValue("$json", JsonSerializer.Serialize(r)); c.Parameters.AddWithValue("$raw", Raw(r.Measurements)); c.ExecuteNonQuery();
            if (++_writes >= 4096) { _transaction!.Commit(); _transaction.Dispose(); _transaction = Connection.BeginTransaction(); _writes = 0; }
        }
        internal IEnumerable<(string Site, string Wafer)> ReadIdentityCodes()
        {
            if (Count == 0) yield break;
            CodeAt(0, true);
            foreach (var code in _codes!) yield return (_sites[code.Site], _wafers[code.Wafer]);
        }
        public void Add(CsvRecord record) { lock (DiskStorage.Sync) { Write(Count, record); Count++; } }
        public void Replace(int index, CsvRecord record) { lock (DiskStorage.Sync) { DiskStorage.Evict(CacheId); Write(index, record); } }
        public void Clear() { lock (DiskStorage.Sync) { DiskStorage.Evict(CacheId); Execute("DELETE FROM records"); Count = 0; } }
        private void SavePage(Page page)
        {
            if (!page.Dirty || _readerOnly) return;
            for (int i = 0; i < page.Rows.Length; i++) Write(page.Start + i, page.Rows[i]);
            page.Dirty = false;
        }
        public CsvRecord Get(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int start = index / PageRows * PageRows;
            var weak = new WeakReference<RecordStore>(this);
            var page = DiskStorage.Get(CacheId, start, () =>
            {
                using var command = Connection.CreateCommand(); command.CommandText = "SELECT json,raw FROM records WHERE slot>=$start AND slot<$end ORDER BY slot";
                command.Parameters.AddWithValue("$start", start); command.Parameters.AddWithValue("$end", Math.Min(Count, start + PageRows));
                using var reader = command.ExecuteReader();
                var rows = new List<CsvRecord>(PageRows);
                while (reader.Read())
                {
                    var row = JsonSerializer.Deserialize<CsvRecord>(reader.GetString(0))!;
                    var h = JsonSerializer.Deserialize<Handle>(reader.GetString(1))!;
                    row.Measurements = h.Store < 0 ? h.Editable ?? new Dictionary<string, string>() : MeasurementCollection.OnDisk(_schemas[h.Schema], _stores[h.Store], h.Offset, h.Length, h.Presence, h.Count);
                    rows.Add(row);
                }
                var result = new Page { Rows = rows.ToArray(), Start = start };
                foreach (var row in result.Rows) row.Changed = () => result.Dirty = true;
                return result;
            }, p => p.Rows.Sum(r => 768L + (r.Id.Length + r.SerialNumber.Length + r.Product.Length + r.Program.Length + r.Lot.Length + (r.RetestUuid?.Length ?? 0)) * 2L),
            p => { if (weak.TryGetTarget(out var store)) store.SavePage(p); });
            return page.Rows[index - start];
        }
        public void Flush() { lock (DiskStorage.Sync) { DiskStorage.Evict(CacheId); if (_readerOnly) return; _transaction!.Commit(); _transaction.Dispose(); _transaction = Connection.BeginTransaction(); _writes = 0; } }
        public int Find(string id)
        {
            lock (DiskStorage.Sync)
            { using var c = Connection.CreateCommand(); c.CommandText = "SELECT slot FROM records WHERE id=$id"; c.Parameters.AddWithValue("$id", id); return c.ExecuteScalar() is long n ? (int)n : -1; }
        }
        internal PagedVector<int> Slots(string sql)
        {
            Flush(); var slots = new PagedVector<int>();
            lock (DiskStorage.Sync)
            { using var c = Connection.CreateCommand(); c.CommandText = sql; using var reader = c.ExecuteReader(); while (reader.Read()) { AnalysisOperation.CheckCancellation(); slots.Add(reader.GetInt32(0)); } }
            slots.Seal(); return slots;
        }
        internal readonly record struct GroupRow(int Slot, int Count);
        internal PagedVector<GroupRow> GroupRows(bool reverse = false)
        {
            Flush(); var rows = new PagedVector<GroupRow>();
            lock (DiskStorage.Sync)
            {
                using var c = Connection.CreateCommand();
                c.CommandText = "SELECT slot,COUNT(*) OVER(PARTITION BY chip) FROM records ORDER BY MIN(slot) OVER(PARTITION BY chip),slot " + (reverse ? "DESC" : "ASC");
                using var r = c.ExecuteReader(); while (r.Read()) { AnalysisOperation.CheckCancellation(); rows.Add(new(r.GetInt32(0), r.GetInt32(1))); }
            }
            rows.Seal(); return rows;
        }
        public IEnumerable<CsvRecord> Query(string sql)
        {
            using var slots = Slots(sql);
            foreach (int slot in slots) yield return Get(slot);
        }
        internal (int Empty, int Duplicates) SerialIssues()
        {
            Flush(); lock (DiskStorage.Sync)
            {
                using var c = Connection.CreateCommand(); c.CommandText = "SELECT SUM(CASE WHEN serialnumber='' THEN 1 ELSE 0 END),COUNT(CASE WHEN serialnumber<>'' THEN 1 END)-COUNT(DISTINCT CASE WHEN serialnumber<>'' THEN serialnumber END) FROM records";
                using var r = c.ExecuteReader(); r.Read(); return (r.IsDBNull(0) ? 0 : r.GetInt32(0), r.GetInt32(1));
            }
        }
        internal int RunLookup(string? serial = null, string? wafer = null, int? x = null, int? y = null, int? after = null, bool previous = false)
        {
            lock (DiskStorage.Sync)
            {
                using var c = Connection.CreateCommand();
                if (serial != null)
                { c.CommandText = "SELECT slot FROM records WHERE serialnumber=$sn ORDER BY slot LIMIT 1"; c.Parameters.AddWithValue("$sn", serial); }
                else
                {
                    c.CommandText = "SELECT slot FROM records WHERE wafer=$w AND x IS NOT NULL AND y IS NOT NULL"; c.Parameters.AddWithValue("$w", wafer ?? "");
                    if (x.HasValue && y.HasValue) { c.CommandText += " AND x=$x AND y=$y"; c.Parameters.AddWithValue("$x", x.Value); c.Parameters.AddWithValue("$y", y.Value); }
                    if (after.HasValue) { c.CommandText += previous ? " AND slot<$slot" : " AND slot>$slot"; c.Parameters.AddWithValue("$slot", after.Value); }
                    c.CommandText += " ORDER BY slot " + (previous || x.HasValue ? "DESC" : "ASC") + " LIMIT 1";
                }
                return c.ExecuteScalar() is long row ? checked((int)row) : -1;
            }
        }
        internal string? ReadMetric(string key)
        {
            lock (DiskStorage.Sync) { using var c = Connection.CreateCommand(); c.CommandText = "SELECT json FROM metrics WHERE key=$key"; c.Parameters.AddWithValue("$key", key); return c.ExecuteScalar() as string; }
        }
        internal void WriteMetric(string key, string json)
        {
            if (_readerOnly) return;
            lock (DiskStorage.Sync) { using var c = Connection.CreateCommand(); c.CommandText = "INSERT OR REPLACE INTO metrics VALUES($key,$json)"; c.Parameters.AddWithValue("$key", key); c.Parameters.AddWithValue("$json", json); c.ExecuteNonQuery(); }
        }
        internal List<string> CoordinateWafers()
        {
            lock (DiskStorage.Sync)
            {
                using var c = Connection.CreateCommand(); c.CommandText = "SELECT wafer FROM records WHERE x IS NOT NULL AND y IS NOT NULL GROUP BY wafer ORDER BY MIN(slot)";
                using var r = c.ExecuteReader(); var result = new List<string>(); while (r.Read()) result.Add(r.GetString(0)); return result;
            }
        }
        protected override void Close() { _codes?.Dispose(); _transaction?.Dispose(); Connection.Dispose(); if (!_readerOnly) try { File.Delete(_path); } catch (System.IO.IOException) { } }
    }
}

public static class RecordSequences
{
    public static RecordList ToRecordList(this IEnumerable<CsvRecord> records)
    { var result = new RecordList(0, disk: true); if (records is RecordList source) result.Inherit(source); foreach (var record in records) { AnalysisOperation.CheckCancellation(); result.Add(record); } result.Flush(); return result; }
}
