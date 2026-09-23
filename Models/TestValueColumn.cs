using System.Collections;

namespace SemiconductorCsvAnalyzer.Models;

/// <summary>One double per source row, sharing the dataset's record table.</summary>
public sealed class TestValueColumn : IList<TestValue>, IReadOnlyList<TestValue>
{
    private double[] _numbers = Array.Empty<double>();
    private IReadOnlyList<CsvRecord> _records = Array.Empty<CsvRecord>();
    private int[]? _validRows;
    private string? _testId;
    private int _count;
    public int Count => _count;
    public bool IsReadOnly => false;
    internal double GetSourceRowValue(int row) => _numbers[row];

    internal void Initialize(IReadOnlyList<CsvRecord> records, string testId)
    {
        _records = records; _testId = testId;
        _numbers = new double[records.Count];
        Array.Fill(_numbers, double.NaN);
        _validRows = null; _count = 0;
    }
    internal void SetRow(int row, double number) { _numbers[row] = number; _count++; }
    internal void Complete()
    {
        if (_count == _numbers.Length) return;
        _validRows = new int[_count];
        int next = 0;
        for (int row = 0; row < _numbers.Length; row++)
            if (double.IsFinite(_numbers[row])) _validRows[next++] = row;
    }
    private int Row(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return _validRows == null ? index : _validRows[index];
    }
    public TestValue this[int index]
    {
        get { int row = Row(index); return new TestValue(_records[row], _numbers[row], _testId); }
        set { Row(index); MakeEditable(); _numbers[index] = value.Value; ((List<CsvRecord>)_records)[index] = value.Record; }
    }

    // Compatibility for small manually constructed datasets and callers. Loaded data
    // uses Initialize/SetRow and never stores a record reference for each test cell.
    private void MakeEditable()
    {
        if (_testId == null && _validRows == null && _records is List<CsvRecord>) return;
        var numbers = new double[_count];
        var records = new List<CsvRecord>(_count);
        for (int i = 0; i < _count; i++) { int row = Row(i); numbers[i] = _numbers[row]; records.Add(_records[row]); }
        _numbers = numbers; _records = records; _validRows = null; _testId = null;
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
    { _numbers = Array.Empty<double>(); _records = Array.Empty<CsvRecord>(); _validRows = null; _testId = null; _count = 0; }
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
    public static IndexedTestValues ForSite(IReadOnlyList<TestValue> source, string site)
    {
        var indices = new List<int>();
        for (int i = 0; i < source.Count; i++)
            if ((string.IsNullOrWhiteSpace(source[i].Site) ? "(空)" : source[i].Site) == site) indices.Add(i);
        return new(source, indices);
    }
    public IEnumerator<TestValue> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
