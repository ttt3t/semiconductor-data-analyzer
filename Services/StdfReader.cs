using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Bounded, streaming STDF V4 record reader (IEEE little/big endian).</summary>
internal sealed class StdfReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _header = new byte[4], _payload = new byte[ushort.MaxValue];
    public bool LittleEndian { get; }
    public long Offset { get; private set; }
    public byte Type { get; private set; }
    public byte Subtype { get; private set; }
    private int _length;
    public StdfFields Fields => new(_payload.AsSpan(0, _length), LittleEndian);

    public StdfReader(FileStream file)
    {
        file.Position = 0;
        int b0 = file.ReadByte(), b1 = file.ReadByte(); file.Position = 0;
        _ownsStream = b0 == 0x1f && b1 == 0x8b;
        // STDF contains millions of small headers/payloads. Buffer decompressed bytes
        // so ReadByte / ReadExactly do not enter the inflater for every small field.
        // Each pass uses a fixed 64 KiB buffer, without retaining the expanded file.
        _stream = _ownsStream
            ? new BufferedStream(new GZipStream(file, CompressionMode.Decompress, leaveOpen: true), 65536)
            : file;
        Span<byte> far = stackalloc byte[6];
        try { _stream.ReadExactly(far); }
        catch (EndOfStreamException ex) { Dispose(); throw new InvalidDataException("STDF 文件不完整：缺少 FAR 文件头。", ex); }
        catch { Dispose(); throw; }
        if (far[2] != 0 || far[3] != 10 || far[4] is not (1 or 2) || far[5] != 4)
        {
            Dispose();
            throw new InvalidDataException($"支持 STDF V4 的 IEEE 大端/小端格式（CPU_TYPE 1/2）；当前文件头版本={far[5]}，CPU_TYPE={far[4]}。V3、VAX 及私有编码不能按 V4 直接读取。");
        }
        LittleEndian = far[4] == 2;
        if ((LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(far) : BinaryPrimitives.ReadUInt16BigEndian(far)) != 2)
        { Dispose(); throw new InvalidDataException("STDF FAR 长度与字节序不一致。"); }
        Offset = 6;
    }

    public bool Next()
    {
        int first = _stream.ReadByte();
        if (first < 0) return false;
        _header[0] = (byte)first;
        try
        {
            _stream.ReadExactly(_header.AsSpan(1));
            _length = LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(_header) : BinaryPrimitives.ReadUInt16BigEndian(_header);
            Type = _header[2]; Subtype = _header[3];
            _stream.ReadExactly(_payload.AsSpan(0, _length));
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException($"STDF 在字节偏移 {Offset} 处截断，记录长度不足。", ex); }
        Offset += 4 + _length;
        return true;
    }

    public void Dispose() { if (_ownsStream) _stream.Dispose(); }
}

internal ref struct StdfFields
{
    private ReadOnlySpan<byte> _data;
    private readonly bool _little;
    public int Remaining => _data.Length;
    public StdfFields(ReadOnlySpan<byte> data, bool little) { _data = data; _little = little; }
    private ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > _data.Length) throw new InvalidDataException("记录字段或数组长度超出 REC_LEN。");
        var result = _data[..length]; _data = _data[length..]; return result;
    }
    public void Skip(int count) => Take(count);
    public byte U1() => Take(1)[0];
    public ushort U2() { var s = Take(2); return _little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s); }
    public short I2() => unchecked((short)U2());
    public uint U4() { var s = Take(4); return _little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s); }
    public float R4() => BitConverter.Int32BitsToSingle(unchecked((int)U4()));
    public byte? OptionalU1() => Remaining == 0 ? null : U1();
    public ushort? OptionalU2() => Remaining == 0 ? null : U2();
    public short? OptionalI2() => Remaining == 0 ? null : I2();
    public uint? OptionalU4() => Remaining == 0 ? null : U4();
    public float? OptionalR4() => Remaining == 0 ? null : R4();
    public string Cn() => Encoding.Latin1.GetString(Take(U1()));
    public string? OptionalCn() => Remaining == 0 ? null : Cn();
    public void OptionalBits() { if (Remaining > 0) Skip((U2() + 7) / 8); }
    public void OptionalBytes() { if (Remaining > 0) Skip(U1()); }
    public ushort[] Indices(int count)
    {
        // A trailing optional array may be absent, but may never be partially present.
        if (Remaining == 0) return Array.Empty<ushort>();
        var result = new ushort[count]; for (int i = 0; i < count; i++) result[i] = U2(); return result;
    }
}
