using System.Collections;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Immutable table snapshots. JSON is row metadata only; numeric measurements stay in column segments.</summary>
public sealed class PagedObjectRows<T> : IList, IList<T>, IReadOnlyList<T>, IDisposable
{
    private List<T>? _small = new();
    private Store? _store;
    private sealed record Position(int Index);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Position> _positions = new();
    public int Count { get; private set; }
    public PagedObjectRows(IEnumerable<T> rows, bool forceDisk = false)
    {
        if (forceDisk) { _small = null; _store = new Store(); }
        try
        {
            foreach (var row in rows)
            {
                if (_small?.Count == 2048)
                { _store = new Store(); foreach (var old in _small) _store.Add(old); _small = null; }
                if (_small != null) _small.Add(row); else _store!.Add(row); Count++;
            }
            _store?.Complete();
        }
        catch { Dispose(); throw; }
    }
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            T row = _small != null ? _small[index] : _store!.Get(index);
            if (!typeof(T).IsValueType && row is object key) _positions.GetValue(key, _ => new(index));
            return row;
        }
    }
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(object? value)
    { if (value != null && _positions.TryGetValue(value, out var position)) return position.Index; for (int i = 0; i < Count; i++) if (Equals(this[i], value)) return i; return -1; }
    public void Dispose() { _store?.Dispose(); _store = null; _small = new(); Count = 0; }
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    T IList<T>.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int IList<T>.IndexOf(T item) => IndexOf(item);
    void IList<T>.Insert(int index, T item) => throw new NotSupportedException();
    void IList<T>.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection<T>.Add(T item) => throw new NotSupportedException();
    void ICollection<T>.Clear() => throw new NotSupportedException();
    bool ICollection<T>.Remove(T item) => throw new NotSupportedException();
    bool ICollection<T>.Contains(T item) => IndexOf(item) >= 0;
    void ICollection<T>.CopyTo(T[] array, int index) { foreach (var row in this) array[index++] = row; }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => IndexOf(value) >= 0;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    private sealed class Store : DiskResource
    {
        private readonly string _path = DiskStorage.NewPath(".table");
        private readonly SqliteConnection _db;
        private SqliteTransaction? _transaction;
        private int _count;
        public Store() : base(false)
        {
            _db = DiskStorage.OpenDatabase(_path); using var c = _db.CreateCommand(); c.CommandText = "CREATE TABLE rows(slot INTEGER PRIMARY KEY,json TEXT)"; c.ExecuteNonQuery(); _transaction = _db.BeginTransaction();
        }
        public void Add(T row)
        { using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO rows VALUES($slot,$json)"; c.Parameters.AddWithValue("$slot", _count++); c.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row)); c.ExecuteNonQuery(); }
        public void Complete() { _transaction!.Commit(); _transaction.Dispose(); _transaction = null; }
        public T Get(int index)
        {
            lock (DiskStorage.Sync)
            {
                var page = DiskStorage.Get(CacheId, index / 64, () =>
                {
                    using var c = _db.CreateCommand(); c.CommandText = "SELECT json FROM rows WHERE slot>=$start AND slot<$end ORDER BY slot";
                    c.Parameters.AddWithValue("$start", index / 64 * 64); c.Parameters.AddWithValue("$end", (index / 64 + 1) * 64);
                    using var r = c.ExecuteReader(); var rows = new List<T>(64); while (r.Read()) rows.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!); return rows.ToArray();
                }, rows => rows.Length * 2048L);
                return page[index % 64];
            }
        }
        protected override void Close() { _transaction?.Dispose(); _db.Dispose(); try { File.Delete(_path); } catch (IOException) { } }
    }
}
