using System.Collections;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Immutable statistical rows with a paged object cache. Only displayed rows become binding objects.</summary>
public sealed class SummaryRows : IList, IReadOnlyList<TestItemSummary>
{
    private readonly TestItemSummary?[]? _small;
    private readonly Store? _store;
    private readonly Dictionary<string, TestItemData> _items = new(StringComparer.Ordinal);
    public int Count { get; }
    public Func<TestItemSummary, double?>? RunValueProvider { get; set; }
    public bool IsPaged => _store != null;
    internal IDisposable AcquireLease() => new Lease(_store);
    private sealed class Lease : IDisposable
    {
        private DiskResource? _resource;
        public Lease(DiskResource? resource) { _resource = resource; resource?.Acquire(); }
        public void Dispose() => Interlocked.Exchange(ref _resource, null)?.Release();
    }
    public SummaryRows(int count = 0)
    { Count = count; if (count <= 2048) _small = new TestItemSummary[count]; else _store = new Store(); }
    public TestItemSummary this[int index]
    {
        get
        {
            var row = Read(index, true);
            return row;
        }
        set
        {
            lock (DiskStorage.Sync)
            {
                _items.TryAdd(value.Data.Id, value.Data);
                if (_small != null) _small[index] = value;
                else _store!.Set(index, value);
            }
        }
    }
    internal TestItemSummary Read(int index, bool track = false)
    {
        var row = _small != null ? _small[index]! : _store!.Get(index, this, track);
        if (RunValueProvider != null) row.RunValue = RunValueProvider(row);
        return row;
    }
    public IEnumerable<TestItemSummary> HotRows => _small != null ? _small.OfType<TestItemSummary>() : _store!.HotRows();
    public int IndexOf(TestItemSummary row)
    {
        if (_small != null) return Array.IndexOf(_small, row);
        return _store!.IndexOf(row);
    }
    public IEnumerator<TestItemSummary> GetEnumerator() { for (int i = 0; i < Count; i++) yield return Read(i); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is TestItemSummary r && IndexOf(r) >= 0;
    int IList.IndexOf(object? value) => value is TestItemSummary r ? IndexOf(r) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    private sealed class Store : DiskResource
    {
        private readonly string _path = DiskStorage.NewPath(".statistics");
        private readonly SqliteConnection _database;
        private readonly SqliteTransaction _transaction;
        private readonly Dictionary<int, WeakReference<TestItemSummary>> _hot = new();
        public Store()
        {
            _database = DiskStorage.OpenDatabase(_path);
            using var c = _database.CreateCommand(); c.CommandText = "CREATE TABLE summary(slot INTEGER PRIMARY KEY,item TEXT,site TEXT,wafer TEXT,combined INTEGER,json TEXT); CREATE INDEX summary_identity ON summary(item,site,wafer,combined)"; c.ExecuteNonQuery();
            _transaction = _database.BeginTransaction();
        }
        public void Set(int index, TestItemSummary row)
        {
            using var c = _database.CreateCommand(); c.CommandText = "INSERT OR REPLACE INTO summary VALUES($slot,$item,$site,$wafer,$combined,$json)";
            c.Parameters.AddWithValue("$slot", index); c.Parameters.AddWithValue("$item", row.Data.Id); c.Parameters.AddWithValue("$site", row.Site);
            c.Parameters.AddWithValue("$wafer", row.Wafer); c.Parameters.AddWithValue("$combined", row.IsWaferCombined); c.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, DiskStorage.JsonOptions)); c.ExecuteNonQuery();
        }
        public TestItemSummary Get(int index, SummaryRows owner, bool track)
        {
            lock (DiskStorage.Sync)
            {
                if (_hot.TryGetValue(index, out var weak) && weak.TryGetTarget(out var existing)) return existing;
                var page = DiskStorage.Get(CacheId, index / 64, () =>
                {
                    using var c = _database.CreateCommand(); c.CommandText = "SELECT item,json FROM summary WHERE slot>=$start AND slot<$end ORDER BY slot";
                    c.Parameters.AddWithValue("$start", index / 64 * 64); c.Parameters.AddWithValue("$end", Math.Min(owner.Count, (index / 64 + 1) * 64));
                    using var r = c.ExecuteReader(); var rows = new List<TestItemSummary>(64);
                    while (r.Read()) { var row = JsonSerializer.Deserialize<TestItemSummary>(r.GetString(1), DiskStorage.JsonOptions)!; row.Data = owner._items[r.GetString(0)]; rows.Add(row); }
                    return rows.ToArray();
                }, r => r.Length * 1024L);
                var result = page[index % 64]; if (track) _hot[index] = new(result);
                if (_hot.Count > 4096) foreach (int key in _hot.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToArray()) _hot.Remove(key);
                return result;
            }
        }
        public IEnumerable<TestItemSummary> HotRows()
        { lock (DiskStorage.Sync) return _hot.Values.Select(w => w.TryGetTarget(out var r) ? r : null).OfType<TestItemSummary>().ToArray(); }
        public int IndexOf(TestItemSummary row)
        {
            lock (DiskStorage.Sync)
            {
                using var c = _database.CreateCommand(); c.CommandText = "SELECT slot FROM summary WHERE item=$item AND site=$site AND wafer=$wafer AND combined=$combined";
                c.Parameters.AddWithValue("$item", row.Data.Id); c.Parameters.AddWithValue("$site", row.Site); c.Parameters.AddWithValue("$wafer", row.Wafer); c.Parameters.AddWithValue("$combined", row.IsWaferCombined);
                return c.ExecuteScalar() is long n ? (int)n : -1;
            }
        }
        protected override void Close() { _transaction.Dispose(); _database.Dispose(); try { File.Delete(_path); } catch (IOException) { } }
    }
}
