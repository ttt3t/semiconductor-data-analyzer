using System.IO;
using Microsoft.Data.Sqlite;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static partial class ProductYieldService
{
    private sealed class StageAccumulator(ManagedYieldRow row)
    {
        public ManagedYieldRow Row { get; } = row;
        public Dictionary<string, int> Categories { get; } = new(StringComparer.Ordinal);
        public int ValidBins;
        public void Add(Observation observation, bool includePartial, Dictionary<string, string> categories)
        {
            Row.Total++;
            if (observation.HBin.Length == 0) Row.Pending++;
            else if (observation.Disposition == OriginalDisposition.Pass) { Row.Pass++; Row.Good++; }
            else if (observation.Disposition == OriginalDisposition.PartialPass)
            { Row.PartialPass++; if (includePartial) Row.Good++; else Row.Failed++; }
            else Row.Failed++;
            if (!observation.KnownIdentity) Row.MissingIdentity++;
            if (observation.SBin.Length > 0)
            {
                ValidBins++;
                string category = categories.GetValueOrDefault(BinStatisticsService.NormalizeBin(observation.SBin), "未分类");
                Categories[category] = Categories.GetValueOrDefault(category) + 1;
            }
        }
    }

    // The on-disk primary key performs retest and cross-temperature matching. At most one
    // chip's observations are hydrated during the combined-yield pass.
    private sealed class ObservationStore : IDisposable
    {
        private readonly string _path = DiskStorage.NewPath(".management");
        private readonly SqliteConnection _db;
        private SqliteTransaction? _file;
        public ObservationStore()
        {
            _db = DiskStorage.OpenDatabase(_path);
            Execute("CREATE TABLE observations(kind TEXT,lot TEXT,wafer TEXT,identity TEXT,stage TEXT,known INTEGER,hbin TEXT,sbin TEXT,disposition INTEGER,time TEXT,PRIMARY KEY(kind,lot,wafer,identity,stage)); CREATE TABLE incoming(kind TEXT,lot TEXT,wafer TEXT,identity TEXT,known INTEGER,hbin TEXT,sbin TEXT,disposition INTEGER,time TEXT); CREATE INDEX stage_population ON observations(kind,lot,wafer,stage)");
        }
        private void Execute(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        public void BeginFile()
        { _file?.Rollback(); _file?.Dispose(); _file = null; Execute("DELETE FROM incoming"); _file = _db.BeginTransaction(); }
        public void Add(DeviceKey key, Observation observation)
        {
            using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO incoming VALUES($kind,$lot,$wafer,$id,$known,$h,$s,$d,$t)";
            c.Parameters.AddWithValue("$kind", key.Kind); c.Parameters.AddWithValue("$lot", key.Lot); c.Parameters.AddWithValue("$wafer", key.Wafer); c.Parameters.AddWithValue("$id", key.Identity);
            c.Parameters.AddWithValue("$known", observation.KnownIdentity); c.Parameters.AddWithValue("$h", observation.HBin); c.Parameters.AddWithValue("$s", observation.SBin); c.Parameters.AddWithValue("$d", (int)observation.Disposition);
            c.Parameters.AddWithValue("$t", observation.TestTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? ""); c.ExecuteNonQuery();
        }
        public IEnumerable<(DeviceKey Key, Observation Observation)> FileReadings()
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT * FROM incoming ORDER BY rowid"; using var r = c.ExecuteReader();
            while (r.Read()) yield return (new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), Read(r, 4));
        }
        public void CommitFile(string stage, bool first)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"INSERT OR {(first ? "IGNORE" : "REPLACE")} INTO observations SELECT kind,lot,wafer,identity,$stage,known,hbin,sbin,disposition,time FROM incoming ORDER BY rowid";
            c.Parameters.AddWithValue("$stage", stage); c.ExecuteNonQuery(); _file!.Commit(); _file.Dispose(); _file = null;
        }
        public IEnumerable<(string Kind, string Lot, string Wafer)> Groups()
        {
            // Match the original first-seen population order, including first/last retest rules.
            using var c = _db.CreateCommand(); c.CommandText = "SELECT kind,lot,wafer FROM observations GROUP BY kind,lot,wafer ORDER BY MIN(rowid)";
            using var r = c.ExecuteReader(); while (r.Read()) yield return (r.GetString(0), r.GetString(1), r.GetString(2));
        }
        private SqliteCommand Scope((string Kind, string Lot, string Wafer) key, string suffix)
        {
            var c = _db.CreateCommand(); c.CommandText = "SELECT identity,stage,known,hbin,sbin,disposition,time FROM observations WHERE kind=$kind AND lot=$lot AND wafer=$wafer " + suffix;
            c.Parameters.AddWithValue("$kind", key.Kind); c.Parameters.AddWithValue("$lot", key.Lot); c.Parameters.AddWithValue("$wafer", key.Wafer); return c;
        }
        public IEnumerable<Observation> Stage((string Kind, string Lot, string Wafer) key, string stage)
        {
            using var c = Scope(key, "AND stage=$stage ORDER BY rowid"); c.Parameters.AddWithValue("$stage", stage);
            using var r = c.ExecuteReader(); while (r.Read()) yield return Read(r, 2);
        }
        public IEnumerable<Dictionary<string, Observation>> Population((string Kind, string Lot, string Wafer) key)
        {
            using var c = Scope(key, "ORDER BY identity COLLATE ORDINAL,stage COLLATE ORDINAL"); using var r = c.ExecuteReader();
            string? previous = null; var observations = new Dictionary<string, Observation>(StringComparer.Ordinal);
            while (r.Read())
            {
                string id = r.GetString(0);
                if (previous != null && id != previous) { yield return observations; observations = new(StringComparer.Ordinal); }
                previous = id; observations.Add(r.GetString(1), Read(r, 2));
            }
            if (previous != null) yield return observations;
        }
        private static Observation Read(SqliteDataReader reader, int start)
        {
            string time = reader.GetString(start + 4);
            return new(reader.GetBoolean(start), reader.GetString(start + 1), reader.GetString(start + 2), (OriginalDisposition)reader.GetInt32(start + 3),
                time.Length == 0 ? null : DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
        }
        public void Dispose()
        { _file?.Dispose(); _db.Dispose(); try { File.Delete(_path); } catch (IOException) { } }
    }
}
