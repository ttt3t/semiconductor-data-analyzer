using System.Collections;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SemiconductorCsvAnalyzer.Services;

public sealed class DiskKeySet : IReadOnlySet<string>, IDisposable
{
    private readonly string _path = DiskStorage.NewPath(".keys");
    private readonly SqliteConnection _database;
    private SqliteTransaction _transaction;
    private int _writes;
    public int Count { get; private set; }
    public DiskKeySet()
    {
        _database = DiskStorage.OpenDatabase(_path);
        using var c = _database.CreateCommand(); c.CommandText = "CREATE TABLE keys(value TEXT PRIMARY KEY) WITHOUT ROWID"; c.ExecuteNonQuery();
        _transaction = _database.BeginTransaction();
    }
    public bool Add(string value)
    {
        lock (DiskStorage.Sync)
        {
            using var c = _database.CreateCommand(); c.CommandText = "INSERT OR IGNORE INTO keys VALUES($value)"; c.Parameters.AddWithValue("$value", value);
            bool added = c.ExecuteNonQuery() != 0; if (added) Count++;
            if (++_writes >= 4096) { _transaction.Commit(); _transaction.Dispose(); _transaction = _database.BeginTransaction(); _writes = 0; }
            return added;
        }
    }
    public bool Contains(string value)
    {
        lock (DiskStorage.Sync)
        { using var c = _database.CreateCommand(); c.CommandText = "SELECT 1 FROM keys WHERE value=$value"; c.Parameters.AddWithValue("$value", value); return c.ExecuteScalar() != null; }
    }
    public void UnionWith(DiskKeySet other)
    {
        lock (DiskStorage.Sync)
        {
            other._transaction.Commit(); other._transaction.Dispose(); other._transaction = other._database.BeginTransaction();
            _transaction.Commit(); _transaction.Dispose();
            using var attach = _database.CreateCommand(); attach.CommandText = "ATTACH DATABASE $path AS incoming"; attach.Parameters.AddWithValue("$path", other._path); attach.ExecuteNonQuery();
            try
            {
                using var insert = _database.CreateCommand(); insert.CommandText = "INSERT OR IGNORE INTO keys SELECT value FROM incoming.keys; SELECT COUNT(*) FROM keys";
                Count = Convert.ToInt32(insert.ExecuteScalar());
            }
            finally
            {
                using var detach = _database.CreateCommand(); detach.CommandText = "DETACH DATABASE incoming"; detach.ExecuteNonQuery();
                _transaction = _database.BeginTransaction(); _writes = 0;
            }
        }
    }
    public void Clear() { lock (DiskStorage.Sync) { using var c = _database.CreateCommand(); c.CommandText = "DELETE FROM keys"; c.ExecuteNonQuery(); Count = 0; } }
    public static DiskKeySet From(IEnumerable<string> values)
    { var result = new DiskKeySet(); try { foreach (string value in values) { AnalysisOperation.CheckCancellation(); result.Add(value); } return result; } catch { result.Dispose(); throw; } }
    public IEnumerator<string> GetEnumerator()
    {
        using var c = _database.CreateCommand(); c.CommandText = "SELECT value FROM keys"; using var r = c.ExecuteReader(); while (r.Read()) yield return r.GetString(0);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsProperSubsetOf(IEnumerable<string> other) { var s = new HashSet<string>(other); return Count < s.Count && this.All(s.Contains); }
    public bool IsProperSupersetOf(IEnumerable<string> other) { var s = new HashSet<string>(other); return Count > s.Count && s.All(Contains); }
    public bool IsSubsetOf(IEnumerable<string> other) => new HashSet<string>(other).IsSupersetOf(this);
    public bool IsSupersetOf(IEnumerable<string> other) => other.All(Contains);
    public bool Overlaps(IEnumerable<string> other) => other.Any(Contains);
    public bool SetEquals(IEnumerable<string> other) { var s = new HashSet<string>(other); return Count == s.Count && s.All(Contains); }
    private bool _disposed;
    public void Dispose() { if (_disposed) return; _disposed = true; _transaction.Dispose(); _database.Dispose(); try { File.Delete(_path); } catch (IOException) { } GC.SuppressFinalize(this); }
    ~DiskKeySet() { try { Dispose(); } catch { } }
}
