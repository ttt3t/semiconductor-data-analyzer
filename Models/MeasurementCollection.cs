using System.Collections;

namespace SemiconductorCsvAnalyzer.Models;

public sealed class MeasurementSchema
{
    internal string[] Keys { get; }
    internal Dictionary<string, int> Indices { get; }
    public MeasurementSchema(IEnumerable<string> keys)
    {
        Keys = keys.ToArray();
        Indices = new Dictionary<string, int>(Keys.Length, StringComparer.Ordinal);
        for (int i = 0; i < Keys.Length; i++) Indices.Add(Keys[i], i);
    }
}

/// <summary>Raw-cell access by disk offset. Dense rows need no per-cell in-memory index.</summary>
public sealed class MeasurementCollection : IReadOnlyDictionary<string, string>
{
    private MeasurementSchema? _schema;
    private RawMeasurementStore? _store;
    private long _offset;
    private int _length, _count;
    private byte[]? _presence;
    private Dictionary<string, string>? _editable;
    internal bool IsOnDisk => _store != null;
    internal RawMeasurementStore? RawStore => _store;

    public MeasurementCollection() => _editable = new();
    private MeasurementCollection(Dictionary<string, string> values) => _editable = values;
    public static implicit operator MeasurementCollection(Dictionary<string, string> values) => new(values);
    public MeasurementCollection(MeasurementSchema schema, IReadOnlyList<string?> values)
    {
        if (schema.Keys.Length != values.Count) throw new ArgumentException("测量值数量与测试项定义不一致。");
        _editable = new();
        for (int i = 0; i < values.Count; i++) if (values[i] is { } value) _editable.Add(schema.Keys[i], value);
    }
    internal static MeasurementCollection OnDisk(MeasurementSchema schema, RawMeasurementStore store,
        long offset, int length, byte[]? presence, int count) => new()
        { _editable = null, _schema = schema, _store = store, _offset = offset, _length = length, _presence = presence, _count = count };

    private bool Present(int i) => _presence == null || (_presence[i >> 3] & (1 << (i & 7))) != 0;
    internal bool PresentAt(int index)
    {
        if (_editable != null)
            return _schema != null && (uint)index < (uint)_schema.Keys.Length && _editable.ContainsKey(_schema.Keys[index]);
        return _presence == null || (_presence[index >> 3] & (1 << (index & 7))) != 0;
    }
    internal RawMeasurementStore.RawRow ReadRow(ref byte[]? reusableBuffer) =>
        _store!.Read(_offset, _length, _schema!.Keys.Length, ref reusableBuffer);
    internal MeasurementCollection SelectKeys(HashSet<string> keys)
    {
        if (_store == null) return new(keys.Where(ContainsKey).ToDictionary(key => key, key => this[key]));
        var presence = new byte[(_schema!.Keys.Length + 7) / 8];
        int count = 0;
        foreach (string key in keys)
            if (_schema.Indices.TryGetValue(key, out int i) && Present(i))
            { presence[i >> 3] |= (byte)(1 << (i & 7)); count++; }
        return OnDisk(_schema, _store, _offset, _length, presence, count);
    }
    internal MeasurementCollection Remap(MeasurementSchema schema)
    {
        if (_store == null) throw new InvalidOperationException("原文尚未保存到快照。");
        if (schema.Keys.Length != _schema!.Keys.Length) throw new ArgumentException("测试项布局不一致。");
        return OnDisk(schema, _store, _offset, _length, _presence, _count);
    }
    internal MeasurementSchema? Schema => _schema;
    public int Count => _editable?.Count ?? _count;
    public IEnumerable<string> Keys => _editable != null ? _editable.Keys : EnumerateOnDiskKeys();
    private IEnumerable<string> EnumerateOnDiskKeys()
    {
        var keys = _schema!.Keys;
        for (int i = 0; i < keys.Length; i++)
            if (Present(i)) yield return keys[i];
    }
    public IEnumerable<string> Values => Keys.Select(key => this[key]);
    public string this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        set { MakeEditable(); _editable![key] = value; }
    }
    public bool ContainsKey(string key) => _editable != null ? _editable.ContainsKey(key) :
        _schema!.Indices.TryGetValue(key, out int i) && Present(i);
    public bool TryGetText(string key, out ReadOnlySpan<char> value)
    {
        if (_editable != null)
        {
            bool found = _editable.TryGetValue(key, out var text); value = text.AsSpan(); return found;
        }
        if (_schema!.Indices.TryGetValue(key, out int i) && Present(i))
        { value = _store!.Read(_offset, _length, _schema.Keys.Length).Text(i); return true; }
        value = default; return false;
    }
    public bool TryGetValue(string key, out string value)
    {
        if (_editable != null) return _editable.TryGetValue(key, out value!);
        bool found = TryGetText(key, out var text); value = found ? text.ToString() : null!; return found;
    }
    public void Add(string key, string value) { MakeEditable(); _editable!.Add(key, value); }
    private void MakeEditable()
    {
        if (_editable != null) return;
        _editable = this.ToDictionary(p => p.Key, p => p.Value);
        _store = null; _schema = null; _presence = null;
    }
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    { foreach (string key in Keys) yield return new(key, this[key]); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
