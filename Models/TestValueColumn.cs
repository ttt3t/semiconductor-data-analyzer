using System.Collections;

namespace SemiconductorCsvAnalyzer.Models;

/// <summary>Column values share the record table; sparse columns retain source row indexes.</summary>
public sealed class TestValueColumn : IList<TestValue>, IReadOnlyList<TestValue>
{
    private double[] _numbers = Array.Empty<double>();
    private float[]? _singleNumbers;
    private IReadOnlyList<CsvRecord> _records = Array.Empty<CsvRecord>();
    private int[]? _validRows;
    private bool _compact, _completed;
    private string? _testId;
    private int _count;
    public int Count => _count;
    public bool IsReadOnly => false;
    internal double GetSourceRowValue(int row)
    {
        if ((uint)row >= (uint)_records.Count) throw new ArgumentOutOfRangeException(nameof(row));
        int offset = _compact ? Array.BinarySearch(_validRows!, row) : row;
        return offset < 0 ? double.NaN : Number(offset);
    }
    internal double GetValueAt(int index)
    {
        int row = Row(index);
        return Number(_compact ? index : row);
    }
    internal CsvRecord GetRecordAt(int index) => _records[Row(index)];
    private double Number(int offset) => _singleNumbers is { } singles ? singles[offset] : _numbers[offset];

    internal bool TryGetContiguousValues(out ReadOnlySpan<double> values)
    {
        if (_singleNumbers == null && (_validRows == null || _compact))
        {
            values = _numbers.AsSpan(0, _count);
            return true;
        }
        values = default;
        return false;
    }

    internal void CopyValuesTo(Span<double> destination)
    {
        if (destination.Length < _count) throw new ArgumentException("Destination is too short.", nameof(destination));
        if (TryGetContiguousValues(out var values))
        {
            values.CopyTo(destination);
            return;
        }
        var rows = _validRows;
        bool consecutive = rows == null || _compact;
        for (int i = 0; i < _count; i++)
            destination[i] = Number(consecutive ? i : rows![i]);
    }

    internal void Initialize(IReadOnlyList<CsvRecord> records, string testId, bool preferSingle = false)
    {
        _records = records; _testId = testId;
        if (preferSingle)
        {
            _numbers = Array.Empty<double>();
            _singleNumbers = new float[records.Count];
            Array.Fill(_singleNumbers, float.NaN);
        }
        else
        {
            _singleNumbers = null;
            _numbers = new double[records.Count];
            Array.Fill(_numbers, double.NaN);
        }
        _validRows = null; _count = 0;
        _compact = _completed = false;
    }
    internal void SetRow(int row, double number)
    {
        if (_completed) throw new InvalidOperationException("The column is already complete.");
        if (!double.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(number));
        bool wasMissing = double.IsNaN(Number(row));
        if (_singleNumbers is { } singles)
        {
            float value = (float)number;
            // STDF R4 values are exact. Mixed imports or other higher precision
            // values promote the whole column rather than rounding a limit crossing.
            if ((double)value == number) singles[row] = value;
            else
            {
                _numbers = new double[singles.Length];
                for (int i = 0; i < singles.Length; i++) _numbers[i] = singles[i];
                _singleNumbers = null;
                _numbers[row] = number;
            }
        }
        else _numbers[row] = number;
        if (wasMissing) _count++;
    }
    internal void Complete()
    {
        if (_completed) return;
        _completed = true;
        int sourceCount = _records.Count;
        if (_count == sourceCount) return;
        _validRows = new int[_count];
        // Strictly below 60%: keep doubles when needed, but discard empty slots.
        _compact = (long)_count * 5 < (long)sourceCount * 3;
        var compactDoubles = _compact && _singleNumbers == null ? new double[_count] : null;
        var compactSingles = _compact && _singleNumbers != null ? new float[_count] : null;
        int next = 0;
        for (int row = 0; row < sourceCount; row++)
        {
            double number = Number(row);
            if (!double.IsFinite(number)) continue;
            _validRows[next] = row;
            if (compactDoubles != null) compactDoubles[next] = number;
            if (compactSingles != null) compactSingles[next] = (float)number;
            next++;
        }
        if (compactDoubles != null) _numbers = compactDoubles;
        if (compactSingles != null) _singleNumbers = compactSingles;
    }
    private int Row(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return _validRows == null ? index : _validRows[index];
    }
    public TestValue this[int index]
    {
        get { int row = Row(index); return new TestValue(_records[row], Number(_compact ? index : row), _testId); }
        set { Row(index); MakeEditable(); _numbers[index] = value.Value; ((List<CsvRecord>)_records)[index] = value.Record; }
    }

    // Compatibility for small manually constructed datasets and callers. Loaded data
    // uses Initialize/SetRow and never stores a record reference for each test cell.
    private void MakeEditable()
    {
        if (_testId == null && _validRows == null && _singleNumbers == null && _records is List<CsvRecord>) return;
        var numbers = new double[_count];
        var records = new List<CsvRecord>(_count);
        for (int i = 0; i < _count; i++) { int row = Row(i); numbers[i] = Number(_compact ? i : row); records.Add(_records[row]); }
        _numbers = numbers; _singleNumbers = null; _records = records; _validRows = null; _testId = null;
        _compact = _completed = false;
    }
    public void Add(TestValue value)
    {
        MakeEditable();
        if (_count == _numbers.Length) Array.Resize(ref _numbers, Math.Max(4, checked(_count * 2)));
        _numbers[_count++] = value.Value;
        ((List<CsvRecord>)_records).Add(value.Record);
    }
    public void AddRange(IEnumerable<TestValue> values) { foreach (var value in values) Add(value); }
    public void Clear()
    { _numbers = Array.Empty<double>(); _singleNumbers = null; _records = Array.Empty<CsvRecord>(); _validRows = null; _testId = null; _count = 0; _compact = _completed = false; }
    public int IndexOf(TestValue value)
    { for (int i = 0; i < Count; i++) if (this[i].Equals(value)) return i; return -1; }
    public bool Contains(TestValue value) => IndexOf(value) >= 0;
    public void CopyTo(TestValue[] array, int index) { for (int i = 0; i < Count; i++) array[index + i] = this[i]; }
    public void Insert(int index, TestValue value)
    {
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        MakeEditable();
        if (_count == _numbers.Length) Array.Resize(ref _numbers, Math.Max(4, checked(_count * 2)));
        Array.Copy(_numbers, index, _numbers, index + 1, _count - index);
        _numbers[index] = value.Value; ((List<CsvRecord>)_records).Insert(index, value.Record); _count++;
    }
    public void RemoveAt(int index)
    {
        Row(index); MakeEditable();
        Array.Copy(_numbers, index + 1, _numbers, index, _count - index - 1);
        ((List<CsvRecord>)_records).RemoveAt(index); _count--;
    }
    public bool Remove(TestValue value) { int i = IndexOf(value); if (i < 0) return false; RemoveAt(i); return true; }
    public IEnumerator<TestValue> GetEnumerator() { for (int i = 0; i < _count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Filtering keeps row indexes, without copying measurement objects or values.</summary>
public sealed class IndexedTestValues : IReadOnlyList<TestValue>
{
    private readonly IReadOnlyList<TestValue> _source;
    private readonly IReadOnlyList<int> _indices;
    public IndexedTestValues(IReadOnlyList<TestValue> source, IReadOnlyList<int> indices)
    { _source = source; _indices = indices; }
    public int Count => _indices.Count;
    public TestValue this[int index] => _source[_indices[index]];
    internal double GetValueAt(int index) => _source is TestValueColumn col ? col.GetValueAt(_indices[index]) : _source[_indices[index]].Value;
    internal CsvRecord GetRecordAt(int index) => _source is TestValueColumn col ? col.GetRecordAt(_indices[index]) : _source[_indices[index]].Record;

    internal void CopyValuesTo(Span<double> destination)
    {
        int n = _indices.Count;
        if (_source is TestValueColumn col)
        {
            for (int i = 0; i < n; i++)
                destination[i] = col.GetValueAt(_indices[i]);
        }
        else
        {
            for (int i = 0; i < n; i++)
                destination[i] = _source[_indices[i]].Value;
        }
    }

    public static IndexedTestValues ForSite(IReadOnlyList<TestValue> source, string site)
        => ForScope(source, site, null);

    public static IndexedTestValues ForScope(IReadOnlyList<TestValue> source, string? site, string? wafer)
    {
        var indices = new List<int>();
        if (source is TestValueColumn col)
        {
            for (int i = 0; i < col.Count; i++)
            {
                var record = col.GetRecordAt(i);
                if ((site == null || (string.IsNullOrWhiteSpace(record.Site) ? "(空)" : record.Site) == site) &&
                    (wafer == null || record.WaferId == wafer))
                    indices.Add(i);
            }
            return new(source, indices);
        }
        for (int i = 0; i < source.Count; i++)
        {
            var value = source[i];
            if ((site == null || (string.IsNullOrWhiteSpace(value.Site) ? "(空)" : value.Site) == site) &&
                (wafer == null || value.WaferId == wafer)) indices.Add(i);
        }
        return new(source, indices);
    }
    public IEnumerator<TestValue> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
