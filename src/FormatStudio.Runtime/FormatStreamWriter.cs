using System.Buffers.Binary;
using System.Text;

namespace FormatStudio.Runtime;

/// <summary>
/// Writes wire primitives to a <see cref="Stream"/>. Counterpart of <see cref="FormatStreamReader"/>; same methods as
/// <see cref="FormatWriter"/>. Wrap unbuffered streams (sockets, files) in a <see cref="BufferedStream"/>.
/// </summary>
public sealed class FormatStreamWriter(Stream stream)
{
    private static ReadOnlySpan<byte> EmptyNbtJava => [0x0A, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtJavaNetwork => [0x0A, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtBedrock => [0x0A, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> EmptyNbtBedrockNetwork => [0x0A, 0x00, 0x00];

    private readonly byte[] _scratch = new byte[16];

    /// <summary>Number of bytes written by this writer.</summary>
    public long Written { get; private set; }

    public static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    private Span<byte> Get(int count) => _scratch.AsSpan(0, count);

    private void Put(int count)
    {
        stream.Write(_scratch, 0, count);
        Written += count;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        stream.Write(bytes);
        Written += bytes.Length;
    }

    public void WriteBool(bool value) => WriteU8(value ? (byte)1 : (byte)0);

    public void WriteU8(byte value)
    {
        stream.WriteByte(value);
        Written++;
    }

    public void WriteI8(sbyte value) => WriteU8((byte)value);

    public void WriteU16LE(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Get(2), value); Put(2); }
    public void WriteU16BE(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(Get(2), value); Put(2); }
    public void WriteI16LE(short value) { BinaryPrimitives.WriteInt16LittleEndian(Get(2), value); Put(2); }
    public void WriteI16BE(short value) { BinaryPrimitives.WriteInt16BigEndian(Get(2), value); Put(2); }
    public void WriteU32LE(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Get(4), value); Put(4); }
    public void WriteU32BE(uint value) { BinaryPrimitives.WriteUInt32BigEndian(Get(4), value); Put(4); }
    public void WriteI32LE(int value) { BinaryPrimitives.WriteInt32LittleEndian(Get(4), value); Put(4); }
    public void WriteI32BE(int value) { BinaryPrimitives.WriteInt32BigEndian(Get(4), value); Put(4); }
    public void WriteU64LE(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Get(8), value); Put(8); }
    public void WriteU64BE(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(Get(8), value); Put(8); }
    public void WriteI64LE(long value) { BinaryPrimitives.WriteInt64LittleEndian(Get(8), value); Put(8); }
    public void WriteI64BE(long value) { BinaryPrimitives.WriteInt64BigEndian(Get(8), value); Put(8); }
    public void WriteF32LE(float value) { BinaryPrimitives.WriteSingleLittleEndian(Get(4), value); Put(4); }
    public void WriteF32BE(float value) { BinaryPrimitives.WriteSingleBigEndian(Get(4), value); Put(4); }
    public void WriteF64LE(double value) { BinaryPrimitives.WriteDoubleLittleEndian(Get(8), value); Put(8); }
    public void WriteF64BE(double value) { BinaryPrimitives.WriteDoubleBigEndian(Get(8), value); Put(8); }

    public void WriteVarUInt32(uint value) => WriteVarUInt64(value);

    public void WriteVarUInt64(ulong value)
    {
        var count = 0;
        while (value >= 0x80)
        {
            _scratch[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        _scratch[count++] = (byte)value;
        Put(count);
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
        Put(16);
    }

    public void WriteUuidBE(Guid value)
    {
        value.TryWriteBytes(Get(16), bigEndian: true, out _);
        Put(16);
    }

    /// <summary>Writes the UTF-8 bytes only; the caller writes the length.</summary>
    public void WriteUtf8(string value) => WriteBytes(Encoding.UTF8.GetBytes(value));

    public void WriteUtf8Zero(string value)
    {
        WriteUtf8(value);
        WriteU8(0);
    }

    // Raw NBT as read by FormatStreamReader.ReadNbt*; null writes an empty compound.
    public void WriteNbtJava(byte[]? raw) => WriteBytes(raw ?? EmptyNbtJava);
    public void WriteNbtJavaNetwork(byte[]? raw) => WriteBytes(raw ?? EmptyNbtJavaNetwork);
    public void WriteNbtBedrock(byte[]? raw) => WriteBytes(raw ?? EmptyNbtBedrock);
    public void WriteNbtBedrockNetwork(byte[]? raw) => WriteBytes(raw ?? EmptyNbtBedrockNetwork);
}
