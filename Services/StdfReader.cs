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
    private readonly StdfWarnings _warnings;
    private byte[] _prefix = Array.Empty<byte>();
    private int _prefixPosition, _prefixLength;
    private bool _stopped, _inputFailed;
    private readonly byte[] _header = new byte[4], _payload = new byte[ushort.MaxValue];
    public bool LittleEndian { get; }
    public long Offset { get; private set; }
    public byte Type { get; private set; }
    public byte Subtype { get; private set; }
    private int _length;
    public StdfFields Fields => new(_payload.AsSpan(0, _length), LittleEndian);

    public StdfReader(FileStream file, StdfWarnings? warnings = null)
    {
        _warnings = warnings ?? new StdfWarnings();
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
        int read;
        try { read = _stream.ReadAtLeast(far, far.Length, throwOnEndOfStream: false); }
        catch { Dispose(); throw; }
        if (read >= 4 && far[2] == 0 && far[3] == 10)
        {
            if (read < 6 || far[4] is not (1 or 2) || far[5] != 4)
            {
                Dispose();
                throw new InvalidDataException("FAR 无法确认受支持的 STDF V4 IEEE 编码（CPU_TYPE 1/2），没有可可靠解码的记录。");
            }
            LittleEndian = far[4] == 2;
            if ((LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(far) : BinaryPrimitives.ReadUInt16BigEndian(far)) != 2)
                _warnings.Add("far-length", "FAR 长度与标准不一致，按 6 字节 V4 文件头继续读取。");
            Offset = 6;
            return;
        }
        if (read < 4 || !KnownRecord(far[2], far[3]))
        { Dispose(); throw new InvalidDataException("未找到 FAR 或可识别的 STDF 记录头，没有可恢复的数据。"); }
        // Only files without FAR need a bounded look-ahead. Replay these bytes in both passes.
        _prefix = new byte[8192]; far[..read].CopyTo(_prefix); _prefixLength = read;
        try
        {
            while (_prefixLength < _prefix.Length)
            {
                int count = _stream.Read(_prefix.AsSpan(_prefixLength));
                if (count == 0) break;
                _prefixLength += count;
            }
        }
        catch (InvalidDataException ex)
        { _inputFailed = true; _warnings.Add("gzip-tail", "压缩数据尾部损坏，保留已解压记录：" + ex.Message); }
        var sample = _prefix.AsSpan(0, _prefixLength);
        int little = Probe(sample, true), big = Probe(sample, false);
        LittleEndian = little >= big;
        _warnings.Add("missing-far", $"缺少 FAR，按记录边界{(little == big ? "暂按" : "推断为")} {(LittleEndian ? "小端" : "大端")} STDF V4 读取，请核对文件编码。");
    }

    private static bool KnownRecord(byte type, byte subtype) => (type, subtype) switch
    {
        (0, 10 or 20 or 30) or (1, 10 or 20 or 30 or 40 or 50 or 60 or 62 or 63 or 70 or 80) or
        (2, 10 or 20 or 30) or (5, 10 or 20) or (10, 30) or (15, 10 or 15 or 20 or 30) or
        (20, 10 or 20) or (50, 10 or 30) or (180, _) or (181, _) => true, _ => false
    };

    private static int Probe(ReadOnlySpan<byte> data, bool little)
    {
        int score = 0, position = 0;
        while (position + 4 <= data.Length)
        {
            var header = data[position..];
            if (!KnownRecord(header[2], header[3])) return score - 4;
            score += 2;
            int length = little ? BinaryPrimitives.ReadUInt16LittleEndian(header) : BinaryPrimitives.ReadUInt16BigEndian(header);
            if (position + 4 + length > data.Length) break;
            position += 4 + length; score++;
        }
        return score;
    }

    private int ReadByte() => _prefixPosition < _prefixLength ? _prefix[_prefixPosition++] : _inputFailed ? -1 : _stream.ReadByte();
    private void ReadExactly(Span<byte> target)
    {
        int count = Math.Min(target.Length, _prefixLength - _prefixPosition);
        _prefix.AsSpan(_prefixPosition, count).CopyTo(target); _prefixPosition += count;
        if (count == target.Length) return;
        if (_inputFailed) throw new EndOfStreamException();
        _stream.ReadExactly(target[count..]);
    }

    public bool Next() => ReadNext(false);
    public bool NextMetadata() => ReadNext(true);
    private bool ReadNext(bool metadataOnly)
    {
        if (_stopped) return false;
        try
        {
            int first = ReadByte();
            if (first < 0) return false;
            _header[0] = (byte)first;
            ReadExactly(_header.AsSpan(1));
            _length = LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(_header) : BinaryPrimitives.ReadUInt16BigEndian(_header);
            Type = _header[2]; Subtype = _header[3];
            bool relevant = Type == 0 && Subtype == 10 || Type == 1 && Subtype is 10 or 20 || Type == 2 && Subtype is 10 or 20;
            if (metadataOnly && !relevant && _stream.CanSeek && _prefixPosition == _prefixLength)
            {
                if (_length > _stream.Length - _stream.Position) throw new EndOfStreamException();
                _stream.Seek(_length, SeekOrigin.Current);
            }
            else ReadExactly(_payload.AsSpan(0, _length));
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            _stopped = true;
            _warnings.Add("truncated", $"STDF 在字节偏移 {Offset} 处截断或压缩数据损坏，已保留此前可读记录：{ex.Message}");
            return false;
        }
        if (Type == 0 && Subtype == 10 && (_length < 2 || _payload[1] != 4 || _payload[0] != (LittleEndian ? 2 : 1)))
        {
            _stopped = true;
            _warnings.Add("encoding-change", "后续 FAR 编码变化，已保留此前可读记录并停止解码后续部分。");
            return false;
        }
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
    public string Cn()
    {
        byte length = U1();
        return length == 0 ? "" : Encoding.Latin1.GetString(Take(length));
    }
    public string? OptionalCn() => Remaining == 0 ? null : Cn();
    public void SkipCn() => Skip(U1());
    public void OptionalSkipCn() { if (Remaining > 0) Skip(U1()); }
    public void OptionalBits() { if (Remaining > 0) Skip((U2() + 7) / 8); }
    public void OptionalBytes() { if (Remaining > 0) Skip(U1()); }
    public ushort[] Indices(int count)
    {
        // A trailing optional array may be absent, but may never be partially present.
        if (Remaining == 0) return Array.Empty<ushort>();
        var result = new ushort[count]; for (int i = 0; i < count; i++) result[i] = U2(); return result;
    }
}
