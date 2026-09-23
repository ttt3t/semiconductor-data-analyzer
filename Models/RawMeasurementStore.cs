using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace SemiconductorCsvAnalyzer.Models;

/// <summary>Exact raw UTF-16 text on disk; only one immutable row is cached per file.</summary>
internal sealed class RawMeasurementStore : IDisposable
{
    private readonly FileStream _file;
    private readonly object _gate = new();
    private RawRow? _cached;
    private bool _sealed;
    public RawMeasurementStore()
    {
        // A private snapshot remains valid if the source CSV is moved or overwritten.
        // DeleteOnClose also cleans up on process exit, including abnormal exits.
        string path = Path.Combine(Path.GetTempPath(), "CsvAnalyzer-" + Guid.NewGuid().ToString("N") + ".rawcache");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    }
    public MeasurementCollection Append(MeasurementSchema schema, IReadOnlyList<string?> values)
    {
        if (_sealed) throw new InvalidOperationException("原文快照已完成写入。");
        if (schema.Keys.Length != values.Count) throw new ArgumentException("测量值数量与测试项定义不一致。");
        int headerBytes = checked(values.Count * sizeof(int));
        int length = headerBytes;
        foreach (string? value in values) if (value != null) length = checked(length + value.Length * sizeof(char));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        byte[]? presence = null;
        int count = 0, end = 0;
        try
        {
            if (values.Any(v => v == null)) presence = new byte[(values.Count + 7) / 8];
            for (int i = 0; i < values.Count; i++)
            {
                string? value = values[i];
                if (value != null)
                {
                    MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(buffer.AsSpan(headerBytes + end * sizeof(char)));
                    end += value.Length; count++;
                    if (presence != null) presence[i >> 3] |= (byte)(1 << (i & 7));
                }
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * sizeof(int)),
                    value == null ? -checked(end + 1) : checked(end + 1));
            }
            long offset = _file.Position;
            _file.Write(buffer.AsSpan(0, length));
            return MeasurementCollection.OnDisk(schema, this, offset, length, presence, count);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public void Seal() { _file.Flush(); _sealed = true; }
    public RawRow Read(long offset, int length, int fields)
    {
        lock (_gate)
        {
            if (!_sealed) throw new InvalidOperationException("原文快照尚未完成写入。");
            if (_cached != null && _cached.Offset == offset) return _cached;
            byte[] bytes = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = RandomAccess.Read(_file.SafeFileHandle, bytes.AsSpan(read), offset + read);
                if (n == 0) throw new EndOfStreamException("原文快照不完整。");
                read += n;
            }
            return _cached = new RawRow(offset, bytes, fields);
        }
    }
    public void Dispose() { _cached = null; _file.Dispose(); }
    internal sealed class RawRow
    {
        public long Offset { get; }
        private readonly byte[] _bytes;
        private readonly int _fields;
        public RawRow(long offset, byte[] bytes, int fields) { Offset = offset; _bytes = bytes; _fields = fields; }
        private int End(int index) => Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(index * sizeof(int)))) - 1;
        public ReadOnlySpan<char> Text(int index)
        {
            int start = index == 0 ? 0 : End(index - 1);
            return MemoryMarshal.Cast<byte, char>(_bytes.AsSpan(_fields * sizeof(int) + start * sizeof(char),
                (End(index) - start) * sizeof(char)));
        }
    }
}
