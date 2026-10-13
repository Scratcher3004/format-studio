using System.Buffers.Binary;
using System.Text;
using FormatStudio.Model;

namespace FormatStudio.Simulation;

/// <summary>A decoding error at a byte offset.</summary>
public sealed class DecodeException(string message, int offset, string? path = null) : Exception(message)
{
    public int Offset { get; } = offset;
    public string? Path { get; set; } = path;
}

/// <summary>An encoding error: the value doesn't fit the format.</summary>
public sealed class EncodeException(string message, string? path = null) : Exception(message)
{
    public string? Path { get; set; } = path;
}

/// <summary>Reads primitives from a byte array, with nested limits for length-delimited sections.</summary>
public sealed class WireReader(byte[] data)
{
    private readonly Stack<int> _limits = new();

    public byte[] Data { get; } = data;
    public int Position { get; set; }
    public int Limit => _limits.Count > 0 ? _limits.Peek() : Data.Length;
    public int Remaining => Limit - Position;

    public void PushLimit(int length)
    {
        if (length < 0 || length > Remaining) throw Error($"Length {length} exceeds the {Remaining} remaining bytes");
        _limits.Push(Position + length);
    }

    public void PopLimit() => _limits.Pop();

    public DecodeException Error(string message) => new(message, Position);

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw Error($"Needs {count} bytes, {Remaining} remaining");
        var span = Data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];

    public ulong ReadVarUInt(int maxBits)
    {
        ulong result = 0;
        for (var shift = 0; shift < maxBits; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (maxBits < 64 && result >> maxBits != 0) throw Error($"Varint exceeds {maxBits} bits");
                return result;
            }
        }

        throw Error($"Varint longer than {(maxBits + 6) / 7} bytes");
    }

    /// <summary>Reads an integer primitive as a signed or unsigned 64-bit value packed in a decimal.</summary>
    public decimal ReadInteger(PrimitiveKind kind, Endian endian)
    {
        var big = endian == Endian.Big;
        return kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.U8 => ReadByte(),
            PrimitiveKind.I8 => (sbyte)ReadByte(),
            PrimitiveKind.U16 => big ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2)),
            PrimitiveKind.I16 => big ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2)),
            PrimitiveKind.U32 => big ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4)),
            PrimitiveKind.I32 => big ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4)),
            PrimitiveKind.U64 => big ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8)),
            PrimitiveKind.I64 => big ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8)),
            PrimitiveKind.VarUInt32 => (uint)ReadVarUInt(32),
            PrimitiveKind.VarUInt64 => ReadVarUInt(64),
            PrimitiveKind.VarInt32 => (int)(uint)ReadVarUInt(32),
            PrimitiveKind.VarInt64 => (long)ReadVarUInt(64),
            PrimitiveKind.ZigZag32 => ZigZag32((uint)ReadVarUInt(32)),
            PrimitiveKind.ZigZag64 => ZigZag64(ReadVarUInt(64)),
            _ => throw new InvalidOperationException($"{kind} is not an integer"),
        };
    }

    private static int ZigZag32(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
    private static long ZigZag64(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    public double ReadFloat(PrimitiveKind kind, Endian endian) => (kind, endian) switch
    {
        (PrimitiveKind.F32, Endian.Big) => BinaryPrimitives.ReadSingleBigEndian(Take(4)),
        (PrimitiveKind.F32, _) => BinaryPrimitives.ReadSingleLittleEndian(Take(4)),
        (PrimitiveKind.F64, Endian.Big) => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(Take(8)),
    };

    /// <summary>Reads a UUID as two 64-bit halves (most significant first) in the given byte order.</summary>
    public string ReadUuid(Endian endian)
    {
        var bytes = Take(16);
        var msb = endian == Endian.Big ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var lsb = endian == Endian.Big ? BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]) : BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var hex = msb.ToString("x16") + lsb.ToString("x16");
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public string ReadString(int byteCount, StringEncoding encoding) => Strings.Decode(Take(byteCount), encoding);
}

/// <summary>Writes primitives to a growable buffer.</summary>
public sealed class WireWriter
{
    private byte[] _buffer = new byte[256];

    public int Length { get; private set; }

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    private Span<byte> Grow(int count)
    {
        if (Length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public void WriteByte(byte value) => Grow(1)[0] = value;

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes an integer primitive. The value must be in range (see <see cref="Primitives.Range"/>).</summary>
    public void WriteInteger(PrimitiveKind kind, Endian endian, decimal value)
    {
        var (min, max) = Primitives.Range(kind);
        if (value < min || value > max || value != decimal.Truncate(value))
            throw new EncodeException($"{value} doesn't fit {Primitives.Name(kind)}");
        var big = endian == Endian.Big;
        switch (kind)
        {
            case PrimitiveKind.Bool or PrimitiveKind.U8: WriteByte((byte)value); break;
            case PrimitiveKind.I8: WriteByte((byte)(sbyte)value); break;
            case PrimitiveKind.U16:
                if (big) BinaryPrimitives.WriteUInt16BigEndian(Grow(2), (ushort)value);
                else BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), (ushort)value);
                break;
            case PrimitiveKind.I16:
                if (big) BinaryPrimitives.WriteInt16BigEndian(Grow(2), (short)value);
                else BinaryPrimitives.WriteInt16LittleEndian(Grow(2), (short)value);
                break;
            case PrimitiveKind.U32:
                if (big) BinaryPrimitives.WriteUInt32BigEndian(Grow(4), (uint)value);
                else BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), (uint)value);
                break;
            case PrimitiveKind.I32:
                if (big) BinaryPrimitives.WriteInt32BigEndian(Grow(4), (int)value);
                else BinaryPrimitives.WriteInt32LittleEndian(Grow(4), (int)value);
                break;
            case PrimitiveKind.U64:
                if (big) BinaryPrimitives.WriteUInt64BigEndian(Grow(8), (ulong)value);
                else BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), (ulong)value);
                break;
            case PrimitiveKind.I64:
                if (big) BinaryPrimitives.WriteInt64BigEndian(Grow(8), (long)value);
                else BinaryPrimitives.WriteInt64LittleEndian(Grow(8), (long)value);
                break;
            case PrimitiveKind.VarUInt32 or PrimitiveKind.VarUInt64: WriteVarUInt((ulong)value); break;
            case PrimitiveKind.VarInt32: WriteVarUInt((uint)(int)value); break;
            case PrimitiveKind.VarInt64: WriteVarUInt((ulong)(long)value); break;
            case PrimitiveKind.ZigZag32:
                var i = (int)value;
                WriteVarUInt((uint)((i << 1) ^ (i >> 31)));
                break;
            case PrimitiveKind.ZigZag64:
                var l = (long)value;
                WriteVarUInt((ulong)((l << 1) ^ (l >> 63)));
                break;
            default: throw new InvalidOperationException($"{kind} is not an integer");
        }
    }

    public void WriteFloat(PrimitiveKind kind, Endian endian, double value)
    {
        switch (kind, endian)
        {
            case (PrimitiveKind.F32, Endian.Big): BinaryPrimitives.WriteSingleBigEndian(Grow(4), (float)value); break;
            case (PrimitiveKind.F32, _): BinaryPrimitives.WriteSingleLittleEndian(Grow(4), (float)value); break;
            case (PrimitiveKind.F64, Endian.Big): BinaryPrimitives.WriteDoubleBigEndian(Grow(8), value); break;
            default: BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), value); break;
        }
    }

    public void WriteUuid(Endian endian, string text)
    {
        var hex = text.Replace("-", "");
        if (hex.Length != 32 || !ulong.TryParse(hex[..16], System.Globalization.NumberStyles.HexNumber, null, out var msb)
            || !ulong.TryParse(hex[16..], System.Globalization.NumberStyles.HexNumber, null, out var lsb))
            throw new EncodeException($"'{text}' is not a UUID");
        var span = Grow(16);
        if (endian == Endian.Big)
        {
            BinaryPrimitives.WriteUInt64BigEndian(span, msb);
            BinaryPrimitives.WriteUInt64BigEndian(span[8..], lsb);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span, msb);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], lsb);
        }
    }
}

public static class Strings
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Decode(ReadOnlySpan<byte> bytes, StringEncoding encoding)
    {
        try
        {
            return encoding switch
            {
                StringEncoding.Utf8 => StrictUtf8.GetString(bytes),
                StringEncoding.Mutf8 => DecodeMutf8(bytes),
                StringEncoding.Ascii => Encoding.ASCII.GetString(bytes),
                StringEncoding.Latin1 => Encoding.Latin1.GetString(bytes),
                StringEncoding.Utf16Le => Encoding.Unicode.GetString(bytes),
                _ => Encoding.BigEndianUnicode.GetString(bytes),
            };
        }
        catch (DecoderFallbackException e)
        {
            throw new DecodeException($"Invalid {encoding}: {e.Message}", 0);
        }
    }

    public static byte[] Encode(string text, StringEncoding encoding) => encoding switch
    {
        StringEncoding.Utf8 => Encoding.UTF8.GetBytes(text),
        StringEncoding.Mutf8 => EncodeMutf8(text),
        StringEncoding.Ascii => Encoding.ASCII.GetBytes(text),
        StringEncoding.Latin1 => Encoding.Latin1.GetBytes(text),
        StringEncoding.Utf16Le => Encoding.Unicode.GetBytes(text),
        _ => Encoding.BigEndianUnicode.GetBytes(text),
    };

    /// <summary>Java's modified UTF-8: NUL as C0 80, supplementary characters as two 3-byte surrogates.</summary>
    public static byte[] EncodeMutf8(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var c in text)
        {
            if (c is >= (char)1 and <= (char)0x7F)
            {
                bytes.Add((byte)c);
            }
            else if (c <= 0x7FF)
            {
                bytes.Add((byte)(0xC0 | (c >> 6)));
                bytes.Add((byte)(0x80 | (c & 0x3F)));
            }
            else
            {
                bytes.Add((byte)(0xE0 | (c >> 12)));
                bytes.Add((byte)(0x80 | ((c >> 6) & 0x3F)));
                bytes.Add((byte)(0x80 | (c & 0x3F)));
            }
        }

        return bytes.ToArray();
    }

    public static string DecodeMutf8(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                builder.Append((char)b);
                i++;
            }
            else if ((b & 0xE0) == 0xC0 && i + 1 < bytes.Length)
            {
                builder.Append((char)(((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i += 2;
            }
            else if ((b & 0xF0) == 0xE0 && i + 2 < bytes.Length)
            {
                builder.Append((char)(((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 3;
            }
            else
            {
                throw new DecodeException($"Invalid modified UTF-8 byte 0x{b:x2}", i);
            }
        }

        return builder.ToString();
    }
}

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Parses hex with optional whitespace, <c>0x</c> prefixes, dashes, colons and commas.</summary>
    public static byte[] Parse(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var token in text.Split([' ', '\t', '\r', '\n', ',', ':', '-'], StringSplitOptions.RemoveEmptyEntries))
            builder.Append(token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token);
        if (builder.Length % 2 != 0) throw new System.FormatException("Odd number of hex digits");
        return Convert.FromHexString(builder.ToString());
    }
}
