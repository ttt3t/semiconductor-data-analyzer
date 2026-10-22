using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Models;

/// <summary>Exact raw UTF-16 text or binary IEEE-754 double on disk; only one immutable row is cached per file.</summary>
internal sealed class RawMeasurementStore : IDisposable
{
    internal const byte StateAbsent = 0;
    internal const byte StateNumeric = 1;
    internal const byte StateNotApplicable = 2;
    internal const byte StateEmpty = 3;
    internal const byte StateText = 4;

    private const int NumericFlag = 0x4000_0000;
    private const int OffsetMask = 0x3FFF_FFFF;
    private const int DoubleCharSpan = sizeof(double) / sizeof(char);

    private readonly FileStream _file;
    private readonly object _gate = new();
    private RawRow? _cached;
    private bool _sealed;
    private int _leases;
    private bool _disposed;
    private readonly bool _operationOwned;

    public RawMeasurementStore()
    {
        // A private snapshot remains valid if the source CSV is moved or overwritten.
        // DeleteOnClose also cleans up on process exit, including abnormal exits.
        string path = Path.Combine(Path.GetTempPath(), "CsvAnalyzer-" + Guid.NewGuid().ToString("N") + ".rawcache");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        _operationOwned = AnalysisOperation.Track(this);
    }

    public MeasurementCollection Append(MeasurementSchema schema, IReadOnlyList<string?> values)
    {
        if (_sealed) throw new InvalidOperationException("原文快照已完成写入。");
        if (schema.Keys.Length != values.Count) throw new ArgumentException("测量值数量与测试项定义不一致。");
        int headerBytes = checked(values.Count * sizeof(int));
        int length = headerBytes;
        bool hasNull = false;
        for (int i = 0; i < values.Count; i++)
        {
            string? value = values[i];
            if (value != null) length = checked(length + value.Length * sizeof(char));
            else hasNull = true;
        }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        byte[]? presence = hasNull ? new byte[(values.Count + 7) / 8] : null;
        int count = 0, end = 0;
        try
        {
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

    public MeasurementCollection AppendStdf(MeasurementSchema schema, ReadOnlySpan<byte> states,
        ReadOnlySpan<double> numbers, Dictionary<int, string>? invalidTexts, string notApplicableText)
    {
        if (_sealed) throw new InvalidOperationException("原文快照已完成写入。");
        int fieldCount = schema.Keys.Length;
        if (fieldCount != states.Length || fieldCount != numbers.Length)
            throw new ArgumentException("测量值数量与测试项定义不一致。");
        int headerBytes = checked(fieldCount * sizeof(int));
        int length = headerBytes;
        bool hasAbsent = false;
        for (int i = 0; i < fieldCount; i++)
        {
            byte state = states[i];
            if (state == StateAbsent) hasAbsent = true;
            else if (state == StateNumeric) length = checked(length + sizeof(double));
            else if (state == StateNotApplicable) length = checked(length + notApplicableText.Length * sizeof(char));
            else if (state == StateText) length = checked(length + invalidTexts![i].Length * sizeof(char));
        }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        byte[]? presence = hasAbsent ? new byte[(fieldCount + 7) / 8] : null;
        int count = 0, end = 0;
        try
        {
            for (int i = 0; i < fieldCount; i++)
            {
                byte state = states[i];
                if (state == StateAbsent)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * sizeof(int)), -checked(end + 1));
                    continue;
                }
                count++;
                if (presence != null) presence[i >> 3] |= (byte)(1 << (i & 7));
                if (state == StateNumeric)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(
                        buffer.AsSpan(headerBytes + end * sizeof(char), sizeof(double)), numbers[i]);
                    end = checked(end + DoubleCharSpan);
                    BinaryPrimitives.WriteInt32LittleEndian(
                        buffer.AsSpan(i * sizeof(int)), NumericFlag | checked(end + 1));
                }
                else
                {
                    ReadOnlySpan<char> text = state == StateNotApplicable ? notApplicableText.AsSpan()
                        : state == StateText ? invalidTexts![i].AsSpan() : ReadOnlySpan<char>.Empty;
                    if (!text.IsEmpty)
                    {
                        MemoryMarshal.AsBytes(text).CopyTo(buffer.AsSpan(headerBytes + end * sizeof(char)));
                        end = checked(end + text.Length);
                    }
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * sizeof(int)), checked(end + 1));
                }
            }
            long offset = _file.Position;
            _file.Write(buffer.AsSpan(0, length));
            return MeasurementCollection.OnDisk(schema, this, offset, length, presence, count);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Seal() { _file.Flush(); _file.Position = 0; _sealed = true; }

    public RawRow Read(long offset, int length, int fields)
    {
        lock (_gate)
        {
            if (!_sealed) throw new InvalidOperationException("原文快照尚未完成写入。");
            if (_cached != null && _cached.Offset == offset) return _cached;
            byte[] bytes = new byte[length];
            ReadBytesLocked(offset, bytes.AsSpan(0, length));
            return _cached = new RawRow(offset, bytes, fields);
        }
    }

    public RawRow Read(long offset, int length, int fields, ref byte[]? reusableBuffer)
    {
        lock (_gate)
        {
            if (!_sealed) throw new InvalidOperationException("原文快照尚未完成写入。");
            if (_cached != null && _cached.Offset == offset) return _cached;
            if (reusableBuffer == null || reusableBuffer.Length < length)
            {
                if (reusableBuffer != null) ArrayPool<byte>.Shared.Return(reusableBuffer);
                reusableBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(65536, length));
            }
            ReadBytesLocked(offset, reusableBuffer.AsSpan(0, length));
            return new RawRow(offset, reusableBuffer, fields);
        }
    }

    private void ReadBytesLocked(long offset, Span<byte> destination)
    {
        if (_file.Position != offset) _file.Position = offset;
        int read = 0;
        while (read < destination.Length)
        {
            int n = _file.Read(destination[read..]);
            if (n == 0) throw new EndOfStreamException("原文快照不完整。");
            read += n;
        }
    }

    internal void Acquire()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _leases++; } }
    internal void Release()
    { lock (_gate) { if (_leases > 0 && --_leases == 0 && _operationOwned) Dispose(); } }
    internal void DisposeIfUnleased()
    { lock (_gate) { if (_leases == 0) Dispose(); } }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; _cached = null; _file.Dispose(); } }

    internal sealed class RawRow
    {
        public long Offset { get; }
        private readonly byte[] _bytes;
        private readonly int _fields;
        public RawRow(long offset, byte[] bytes, int fields) { Offset = offset; _bytes = bytes; _fields = fields; }
        private int RawHeader(int index) => BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(index * sizeof(int)));
        private int End(int index)
        {
            int raw = RawHeader(index);
            return (raw < 0 ? -raw : (raw & OffsetMask)) - 1;
        }
        public bool IsNumeric(int index)
        {
            int raw = RawHeader(index);
            return raw > 0 && (raw & NumericFlag) != 0;
        }
        public double ReadNumeric(int index)
        {
            int start = index == 0 ? 0 : End(index - 1);
            return BinaryPrimitives.ReadDoubleLittleEndian(
                _bytes.AsSpan(_fields * sizeof(int) + start * sizeof(char), sizeof(double)));
        }
        public ReadOnlySpan<char> RawTextSpan(int index)
        {
            int start = index == 0 ? 0 : End(index - 1);
            return MemoryMarshal.Cast<byte, char>(_bytes.AsSpan(_fields * sizeof(int) + start * sizeof(char),
                (End(index) - start) * sizeof(char)));
        }
        public ReadOnlySpan<char> Text(int index)
        {
            if (IsNumeric(index))
                return ReadNumeric(index).ToString("R", CultureInfo.InvariantCulture).AsSpan();
            return RawTextSpan(index);
        }
    }
}
