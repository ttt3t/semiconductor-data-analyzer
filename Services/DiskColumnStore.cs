using System.Buffers.Binary;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Bounded row-group transpose. Final numeric segments are contiguous per test, with exact source row IDs.</summary>
internal sealed class DiskColumnStore : DiskResource
{
    private sealed class Column
    { public long Offset; public int Count; public bool Single, Dense; public int ValueOffset => Dense ? 0 : 4; public int Stride => ValueOffset + (Single ? 4 : 8); }
    private readonly Column[] _columns;
    private readonly int _batchRows;
    private double[]? _batch;
    private int _start;
    private readonly int _sourceRows;
    private readonly string _spoolPath = DiskStorage.NewPath(".transpose");
    private readonly string _indexPath = DiskStorage.NewPath(".chunks");
    private readonly string _dataPath = DiskStorage.NewPath(".columns");
    private FileStream? _spool;
    private SqliteConnection? _index;
    private SqliteTransaction? _transaction;
    private readonly FileStream _data;
    private bool _complete;
    private readonly bool _readerOnly;
    public DiskColumnStore(int rows, IReadOnlyList<bool> preferSingle)
    {
        _sourceRows = rows; _columns = preferSingle.Select(s => new Column { Single = s }).ToArray();
        _batchRows = Math.Max(1, Math.Min(4096, DiskStorage.WorkBufferBytes / Math.Max(8, preferSingle.Count * 8)));
        _batch = new double[checked(_batchRows * preferSingle.Count)]; Array.Fill(_batch, double.NaN);
        _spool = new FileStream(_spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.RandomAccess);
        _data = new FileStream(_dataPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.RandomAccess);
        _index = DiskStorage.OpenDatabase(_indexPath);
        using var command = _index.CreateCommand(); command.CommandText = "CREATE TABLE chunks(item INTEGER,start INTEGER,offset INTEGER,count INTEGER); CREATE INDEX chunk_order ON chunks(item,start);"; command.ExecuteNonQuery();
        _transaction = _index.BeginTransaction();
    }
    internal sealed record Snapshot(string Path, int Rows, long Offset, int Count, bool Single);
    internal Snapshot Capture(int item) => new(_dataPath, _sourceRows, _columns[item].Offset, _columns[item].Count, _columns[item].Single);
    internal DiskColumnStore(Snapshot snapshot) : this(new[] { snapshot }) { }
    internal DiskColumnStore(IReadOnlyList<Snapshot> snapshots) : base(false)
    {
        var snapshot = snapshots[0];
        _readerOnly = true; _dataPath = snapshot.Path; _sourceRows = snapshot.Rows;
        _columns = snapshots.Select(s => new Column { Offset = s.Offset, Count = s.Count, Single = s.Single, Dense = s.Count == s.Rows }).ToArray();
        _data = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess); _complete = true;
        if (snapshots.Any(s => s.Path != _dataPath || s.Offset < 0 || s.Count < 0 || s.Rows < s.Count || s.Offset + (long)s.Count * ((s.Count == s.Rows ? 0 : 4) + (s.Single ? 4 : 8)) > _data.Length))
            throw new InvalidDataException("数值缓存长度校验失败，请重新加载源文件。");
    }
    public void Set(int item, int row, double value)
    {
        while (row >= _start + _batchRows) FlushBatch();
        _batch![item * _batchRows + row - _start] = value;
        if (_columns[item].Single && (double)(float)value != value) _columns[item].Single = false;
    }
    private void FlushBatch()
    {
        AnalysisOperation.CheckCancellation(); int rows = Math.Min(_batchRows, _sourceRows - _start);
        using var writer = new BinaryWriter(_spool!, System.Text.Encoding.UTF8, true);
        for (int item = 0; item < _columns.Length; item++)
        {
            long offset = _spool!.Position; int count = 0;
            for (int r = 0; r < rows; r++)
            {
                double value = _batch![item * _batchRows + r]; if (!double.IsFinite(value)) continue;
                writer.Write(_start + r); writer.Write(value); count++;
            }
            if (count == 0) continue;
            using var command = _index!.CreateCommand(); command.CommandText = "INSERT INTO chunks VALUES($item,$start,$offset,$count)";
            command.Parameters.AddWithValue("$item", item); command.Parameters.AddWithValue("$start", _start); command.Parameters.AddWithValue("$offset", offset); command.Parameters.AddWithValue("$count", count); command.ExecuteNonQuery();
            _columns[item].Count += count;
        }
        _start += _batchRows; Array.Fill(_batch!, double.NaN);
    }
    public void Complete()
    {
        if (_complete) return;
        while (_start < _sourceRows) FlushBatch();
        _batch = null; _spool!.Flush(); _transaction!.Commit(); _transaction.Dispose(); _transaction = null;
        byte[] buffer = new byte[_batchRows * 12];
        using var writer = new BinaryWriter(_data, System.Text.Encoding.UTF8, true);
        for (int item = 0; item < _columns.Length; item++)
        {
            AnalysisOperation.CheckCancellation(); var column = _columns[item]; column.Offset = _data.Position; column.Dense = column.Count == _sourceRows;
            using var command = _index!.CreateCommand(); command.CommandText = "SELECT offset,count FROM chunks WHERE item=$item ORDER BY start"; command.Parameters.AddWithValue("$item", item);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int count = reader.GetInt32(1); int bytes = count * 12;
                DiskStorage.ReadExactly(_spool.SafeFileHandle, buffer.AsSpan(0, bytes), reader.GetInt64(0));
                for (int i = 0; i < count; i++)
                {
                    if (!column.Dense) writer.Write(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i * 12)));
                    double value = BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(i * 12 + 4));
                    if (column.Single) writer.Write((float)value); else writer.Write(value);
                }
            }
        }
        _data.Flush(); _complete = true; _spool.Dispose(); _spool = null; _index!.Dispose(); _index = null;
        File.Delete(_spoolPath); File.Delete(_indexPath);
    }
    public int Count(int item) => _columns[item].Count;
    public bool Single(int item) => _columns[item].Single;
    private byte[] Page(Column c, int page)
    {
        int perPage = 65536 / c.Stride; long offset = c.Offset + (long)page * perPage * c.Stride;
        return DiskStorage.Get(CacheId, offset, () =>
        {
            byte[] result = new byte[Math.Min(perPage, c.Count - page * perPage) * c.Stride];
            DiskStorage.ReadExactly(_data.SafeFileHandle, result, offset); return result;
        }, b => b.Length);
    }
    public int Row(int item, int index)
    {
        var c = _columns[item]; if (c.Count == _sourceRows) return index;
        int perPage = 65536 / c.Stride;
        return BinaryPrimitives.ReadInt32LittleEndian(Page(c, index / perPage).AsSpan(index % perPage * c.Stride));
    }
    public double Value(int item, int index)
    {
        var c = _columns[item]; int perPage = 65536 / c.Stride;
        var span = Page(c, index / perPage).AsSpan(index % perPage * c.Stride + c.ValueOffset);
        return c.Single ? BinaryPrimitives.ReadSingleLittleEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span);
    }
    public double SourceValue(int item, int row)
    {
        var c = _columns[item]; if (c.Count == _sourceRows) return Value(item, row);
        int lo = 0, hi = c.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (Row(item, mid) < row) lo = mid + 1; else hi = mid; }
        return lo < c.Count && Row(item, lo) == row ? Value(item, lo) : double.NaN;
    }
    public IEnumerable<double> Values(int item)
    {
        var c = _columns[item]; int perPage = 65536 / c.Stride;
        for (int start = 0; start < c.Count; start += perPage)
        {
            var page = Page(c, start / perPage); int count = Math.Min(perPage, c.Count - start);
            for (int i = 0; i < count; i++) yield return c.Single ? BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(i * c.Stride + c.ValueOffset)) : BinaryPrimitives.ReadDoubleLittleEndian(page.AsSpan(i * c.Stride + c.ValueOffset));
        }
    }
    internal IEnumerable<(int Row, double Value)> Entries(int item)
    {
        var c = _columns[item]; int perPage = 65536 / c.Stride;
        for (int start = 0; start < c.Count; start += perPage)
        {
            var page = Page(c, start / perPage); int count = Math.Min(perPage, c.Count - start);
            for (int i = 0; i < count; i++) yield return (c.Dense ? start + i : BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(i * c.Stride)),
                c.Single ? BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(i * c.Stride + c.ValueOffset)) : BinaryPrimitives.ReadDoubleLittleEndian(page.AsSpan(i * c.Stride + c.ValueOffset)));
        }
    }
    protected override void Close()
    {
        _transaction?.Dispose(); _index?.Dispose(); _spool?.Dispose(); _data.Dispose(); _batch = null;
        foreach (string file in _readerOnly ? Array.Empty<string>() : new[] { _spoolPath, _indexPath, _dataPath }) try { File.Delete(file); } catch (IOException) { }
    }
}
