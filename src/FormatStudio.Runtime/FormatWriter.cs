using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FormatStudio.Runtime;

/// <summary>Writes wire primitives to a buffer writer. Counterpart of <see cref="FormatReader"/>.</summary>
public ref struct FormatWriter
{
    // Empty compound tags: type, empty root name (except Java network), end.
    private static ReadOnlySpan<byte> EmptyNbtJava => [0x0A, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtJavaNetwork => [0x0A, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtBedrock => [0x0A, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtBedrockNetwork => [0x0A, 0x00, 0x00];

    private readonly IBufferWriter<byte> _output;

    public FormatWriter(IBufferWriter<byte> output)
    {
        _output = output;
    }

    /// <summary>Number of bytes written by this writer.</summary>
    public int Written { get; private set; }

    public static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    private Span<byte> Get(int count) => _output.GetSpan(count)[..count];

    private void Advance(int count)
    {
        _output.Advance(count);
        Written += count;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Get(bytes.Length));
        Advance(bytes.Length);
    }

    public void WriteBool(bool value) => WriteU8(value ? (byte)1 : (byte)0);

    public void WriteU8(byte value)
    {
        Get(1)[0] = value;
        Advance(1);
    }

    public void WriteI8(sbyte value) => WriteU8((byte)value);

    public void WriteU16LE(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Get(2), value); Advance(2); }
    public void WriteU16BE(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(Get(2), value); Advance(2); }
    public void WriteI16LE(short value) { BinaryPrimitives.WriteInt16LittleEndian(Get(2), value); Advance(2); }
    public void WriteI16BE(short value) { BinaryPrimitives.WriteInt16BigEndian(Get(2), value); Advance(2); }
    public void WriteU32LE(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Get(4), value); Advance(4); }
    public void WriteU32BE(uint value) { BinaryPrimitives.WriteUInt32BigEndian(Get(4), value); Advance(4); }
    public void WriteI32LE(int value) { BinaryPrimitives.WriteInt32LittleEndian(Get(4), value); Advance(4); }
    public void WriteI32BE(int value) { BinaryPrimitives.WriteInt32BigEndian(Get(4), value); Advance(4); }
    public void WriteU64LE(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Get(8), value); Advance(8); }
    public void WriteU64BE(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(Get(8), value); Advance(8); }
    public void WriteI64LE(long value) { BinaryPrimitives.WriteInt64LittleEndian(Get(8), value); Advance(8); }
    public void WriteI64BE(long value) { BinaryPrimitives.WriteInt64BigEndian(Get(8), value); Advance(8); }
    public void WriteF16LE(Half value) { BinaryPrimitives.WriteHalfLittleEndian(Get(2), value); Advance(2); }
    public void WriteF16BE(Half value) { BinaryPrimitives.WriteHalfBigEndian(Get(2), value); Advance(2); }
    public void WriteF32LE(float value) { BinaryPrimitives.WriteSingleLittleEndian(Get(4), value); Advance(4); }
    public void WriteF32BE(float value) { BinaryPrimitives.WriteSingleBigEndian(Get(4), value); Advance(4); }
    public void WriteF64LE(double value) { BinaryPrimitives.WriteDoubleLittleEndian(Get(8), value); Advance(8); }
    public void WriteF64BE(double value) { BinaryPrimitives.WriteDoubleBigEndian(Get(8), value); Advance(8); }

    public void WriteVarUInt32(uint value) => WriteVarUInt64(value);

    public void WriteVarUInt64(ulong value)
    {
        var span = Get(10);
        var count = 0;
        while (value >= 0x80)
        {
            span[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[count++] = (byte)value;
        Advance(count);
    }

    public void WriteVarInt32(int value) => WriteVarUInt32((uint)value);
    public void WriteVarInt64(long value) => WriteVarUInt64((ulong)value);
    public void WriteZigZag32(int value) => WriteVarUInt32((uint)((value << 1) ^ (value >> 31)));
    public void WriteZigZag64(long value) => WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));

    public void WriteUuidLE(Guid value)
    {
        Span<byte> big = stackalloc byte[16];
        value.TryWriteBytes(big, bigEndian: true, out _);
        var span = Get(16);
        BinaryPrimitives.WriteUInt64LittleEndian(span, BinaryPrimitives.ReadUInt64BigEndian(big));
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], BinaryPrimitives.ReadUInt64BigEndian(big[8..]));
        Advance(16);
    }

    public void WriteUuidBE(Guid value)
    {
        value.TryWriteBytes(Get(16), bigEndian: true, out _);
        Advance(16);
    }

    /// <summary>Writes the UTF-8 bytes only; the caller writes the length.</summary>
    public void WriteUtf8(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Encoding.UTF8.GetBytes(value, Get(count));
        Advance(count);
    }

    /// <summary>
    /// Writes the UTF-8 bytes, then <paramref name="pad"/> bytes up to <paramref name="byteCount"/>; the caller checks that
    /// the string fits.
    /// </summary>
    public void WriteUtf8Padded(string value, int byteCount, byte pad)
    {
        WriteUtf8(value);
        for (var i = Utf8Length(value); i < byteCount; i++) WriteU8(pad);
    }

    /// <summary>The number of bytes <see cref="WriteString"/> writes for a string.</summary>
    public static int ByteLength(string value, FormatEncoding encoding) => TextCodec.ByteCount(value, encoding);

    /// <summary>Writes the bytes only; the caller writes the length. Characters the encoding doesn't have are errors.</summary>
    public void WriteString(string value, FormatEncoding encoding) => WriteBytes(TextCodec.Encode(value, encoding));

    /// <summary>Writes the bytes, then <paramref name="pad"/> bytes up to <paramref name="byteCount"/>; the caller checks that the string fits.</summary>
    public void WriteStringPadded(string value, int byteCount, byte pad, FormatEncoding encoding)
    {
        var bytes = TextCodec.Encode(value, encoding);
        WriteBytes(bytes);
        for (var i = bytes.Length; i < byteCount; i++) WriteU8(pad);
    }

    public void WriteStringZero(string value, FormatEncoding encoding)
    {
        WriteString(value, encoding);
        WriteU8(0);
    }

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="pattern"/> repeated: the fill of a sized region.</summary>
    public void WriteFill(ReadOnlySpan<byte> pattern, int count)
    {
        for (var i = 0; i < count; i++) WriteU8(pattern[i % pattern.Length]);
    }

    public void WriteUtf8Zero(string value)
    {
        WriteUtf8(value);
        WriteU8(0);
    }

    // Raw NBT as read by FormatReader.ReadNbt*; null writes an empty compound.
    public void WriteNbtJava(byte[]? raw) => WriteBytes(raw ?? EmptyNbtJava);
    public void WriteNbtJavaNetwork(byte[]? raw) => WriteBytes(raw ?? EmptyNbtJavaNetwork);
    public void WriteNbtBedrock(byte[]? raw) => WriteBytes(raw ?? EmptyNbtBedrock);
    public void WriteNbtBedrockNetwork(byte[]? raw) => WriteBytes(raw ?? EmptyNbtBedrockNetwork);
}
