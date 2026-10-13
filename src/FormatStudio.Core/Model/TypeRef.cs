namespace FormatStudio.Model;

public enum Endian
{
    Little,
    Big,
}

public enum PrimitiveKind
{
    Bool,
    U8,
    I8,
    U16,
    I16,
    U32,
    I32,
    U64,
    I64,
    F32,
    F64,
    /// <summary>Unsigned LEB128, at most 5 bytes.</summary>
    VarUInt32,
    /// <summary>Unsigned LEB128, at most 10 bytes.</summary>
    VarUInt64,
    /// <summary>Two's complement LEB128 of a 32-bit value (Java VarInt).</summary>
    VarInt32,
    /// <summary>Two's complement LEB128 of a 64-bit value (Java VarLong).</summary>
    VarInt64,
    /// <summary>Zigzag-encoded LEB128 of a signed 32-bit value (Bedrock varint).</summary>
    ZigZag32,
    /// <summary>Zigzag-encoded LEB128 of a signed 64-bit value.</summary>
    ZigZag64,
    /// <summary>16 bytes: most significant 64 bits, then least significant 64 bits, each in the given byte order.</summary>
    Uuid,
}

public static class Primitives
{
    private static readonly Dictionary<string, PrimitiveKind> Names = new(StringComparer.Ordinal)
    {
        ["bool"] = PrimitiveKind.Bool,
        ["u8"] = PrimitiveKind.U8,
        ["i8"] = PrimitiveKind.I8,
        ["u16"] = PrimitiveKind.U16,
        ["i16"] = PrimitiveKind.I16,
        ["u32"] = PrimitiveKind.U32,
        ["i32"] = PrimitiveKind.I32,
        ["u64"] = PrimitiveKind.U64,
        ["i64"] = PrimitiveKind.I64,
        ["f32"] = PrimitiveKind.F32,
        ["f64"] = PrimitiveKind.F64,
        ["varuint32"] = PrimitiveKind.VarUInt32,
        ["varuint64"] = PrimitiveKind.VarUInt64,
        ["varint32"] = PrimitiveKind.VarInt32,
        ["varint64"] = PrimitiveKind.VarInt64,
        ["zigzag32"] = PrimitiveKind.ZigZag32,
        ["zigzag64"] = PrimitiveKind.ZigZag64,
        ["uuid"] = PrimitiveKind.Uuid,
    };

    /// <summary>Parses <c>u16</c>, <c>u16le</c>, <c>u16be</c>, <c>varuint32</c>, ...</summary>
    public static bool TryParse(string text, out PrimitiveRef primitive)
    {
        primitive = null!;
        Endian? endian = null;
        var name = text;
        if (text.EndsWith("le", StringComparison.Ordinal) && Names.ContainsKey(text[..^2]))
        {
            endian = Endian.Little;
            name = text[..^2];
        }
        else if (text.EndsWith("be", StringComparison.Ordinal) && Names.ContainsKey(text[..^2]))
        {
            endian = Endian.Big;
            name = text[..^2];
        }

        if (!Names.TryGetValue(name, out var kind)) return false;
        if (endian != null && !HasEndian(kind)) return false;
        primitive = new PrimitiveRef(kind, endian);
        return true;
    }

    public static string Name(PrimitiveKind kind) => Names.First(pair => pair.Value == kind).Key;

    public static bool HasEndian(PrimitiveKind kind) => kind is PrimitiveKind.U16 or PrimitiveKind.I16 or PrimitiveKind.U32
        or PrimitiveKind.I32 or PrimitiveKind.U64 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64
        or PrimitiveKind.Uuid;

    public static bool IsInteger(PrimitiveKind kind) => kind is not (PrimitiveKind.Bool or PrimitiveKind.F32
        or PrimitiveKind.F64 or PrimitiveKind.Uuid);

    public static bool IsFloat(PrimitiveKind kind) => kind is PrimitiveKind.F32 or PrimitiveKind.F64;

    public static bool IsSigned(PrimitiveKind kind) => kind is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32
        or PrimitiveKind.I64 or PrimitiveKind.VarInt32 or PrimitiveKind.VarInt64 or PrimitiveKind.ZigZag32
        or PrimitiveKind.ZigZag64;

    public static bool IsVariable(PrimitiveKind kind) => kind is PrimitiveKind.VarUInt32 or PrimitiveKind.VarUInt64
        or PrimitiveKind.VarInt32 or PrimitiveKind.VarInt64 or PrimitiveKind.ZigZag32 or PrimitiveKind.ZigZag64;

    /// <summary>Number of value bits (not wire bytes).</summary>
    public static int Bits(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => 1,
        PrimitiveKind.U8 or PrimitiveKind.I8 => 8,
        PrimitiveKind.U16 or PrimitiveKind.I16 => 16,
        PrimitiveKind.U32 or PrimitiveKind.I32 or PrimitiveKind.F32 or PrimitiveKind.VarUInt32
            or PrimitiveKind.VarInt32 or PrimitiveKind.ZigZag32 => 32,
        PrimitiveKind.Uuid => 128,
        _ => 64,
    };

    /// <summary>Fixed wire size, or the minimum size (1) for variable-length integers.</summary>
    public static int MinWireSize(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool or PrimitiveKind.U8 or PrimitiveKind.I8 => 1,
        PrimitiveKind.U16 or PrimitiveKind.I16 => 2,
        PrimitiveKind.U32 or PrimitiveKind.I32 or PrimitiveKind.F32 => 4,
        PrimitiveKind.U64 or PrimitiveKind.I64 or PrimitiveKind.F64 => 8,
        PrimitiveKind.Uuid => 16,
        _ => 1,
    };

    public static (decimal Min, decimal Max) Range(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => (0, 1),
        PrimitiveKind.U8 => (byte.MinValue, byte.MaxValue),
        PrimitiveKind.I8 => (sbyte.MinValue, sbyte.MaxValue),
        PrimitiveKind.U16 => (ushort.MinValue, ushort.MaxValue),
        PrimitiveKind.I16 => (short.MinValue, short.MaxValue),
        PrimitiveKind.U32 or PrimitiveKind.VarUInt32 => (uint.MinValue, uint.MaxValue),
        PrimitiveKind.I32 or PrimitiveKind.VarInt32 or PrimitiveKind.ZigZag32 => (int.MinValue, int.MaxValue),
        PrimitiveKind.U64 or PrimitiveKind.VarUInt64 => (ulong.MinValue, ulong.MaxValue),
        PrimitiveKind.I64 or PrimitiveKind.VarInt64 or PrimitiveKind.ZigZag64 => (long.MinValue, long.MaxValue),
        _ => (decimal.MinValue, decimal.MaxValue),
    };
}

/// <summary>A type expression as written in a field, an array element, a variant case, ...</summary>
public abstract record TypeRef
{
    public abstract override string ToString();
}

/// <summary>A primitive. <see cref="Endian"/> is null when the project default applies.</summary>
public sealed record PrimitiveRef(PrimitiveKind Kind, Endian? Endian = null) : TypeRef
{
    public override string ToString() => Primitives.Name(Kind) + Endian switch
    {
        Model.Endian.Little => "le",
        Model.Endian.Big => "be",
        _ => "",
    };
}

/// <summary>
/// A reference to a named definition. <see cref="As"/> overrides the wire encoding of an enum,
/// <see cref="Tag"/> supplies the tag expression of a variant with an external tag.
/// </summary>
public sealed record NamedRef(string Name, PrimitiveRef? As = null, string? Tag = null) : TypeRef
{
    public override string ToString() => As == null && Tag == null ? Name : $"{Name}({As}{Tag})";
}

public enum LengthKind
{
    /// <summary>The project's default length prefix.</summary>
    Default,
    /// <summary>A length prefix of the given primitive type.</summary>
    Prefix,
    /// <summary>A constant number of elements or bytes.</summary>
    Fixed,
    /// <summary>An expression over earlier fields.</summary>
    Expression,
    /// <summary>Everything up to the end of the enclosing data.</summary>
    Eof,
    /// <summary>A zero terminator (strings and bytes only).</summary>
    Zero,
}

public sealed record LengthSpec(LengthKind Kind, PrimitiveRef? Prefix = null, int Fixed = 0, string? Expression = null)
{
    public static readonly LengthSpec Default = new(LengthKind.Default);
    public static readonly LengthSpec Eof = new(LengthKind.Eof);
    public static readonly LengthSpec Zero = new(LengthKind.Zero);

    public override string ToString() => Kind switch
    {
        LengthKind.Default => "default",
        LengthKind.Prefix => Prefix!.ToString(),
        LengthKind.Fixed => Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        LengthKind.Expression => Expression!,
        LengthKind.Eof => "eof",
        _ => "zero",
    };
}

public enum StringEncoding
{
    Utf8,
    /// <summary>Java's modified UTF-8 (NBT strings, DataInput).</summary>
    Mutf8,
    Ascii,
    Latin1,
    Utf16Le,
    Utf16Be,
}

/// <summary>A string whose length is counted in bytes.</summary>
public sealed record StringRef(StringEncoding? Encoding, LengthSpec Length) : TypeRef
{
    public override string ToString() => $"string({Encoding?.ToString() ?? "default"}, {Length})";
}

public sealed record BytesRef(LengthSpec Length) : TypeRef
{
    public override string ToString() => $"bytes({Length})";
}

public sealed record ArrayRef(TypeRef Element, LengthSpec Length) : TypeRef
{
    public override string ToString() => $"{Element}[{Length}]";
}

public sealed record MapRef(TypeRef Key, TypeRef Value, LengthSpec Length) : TypeRef
{
    public override string ToString() => $"map<{Key}, {Value}>[{Length}]";
}

/// <summary>A value preceded by a boolean presence byte.</summary>
public sealed record OptionalRef(TypeRef Inner) : TypeRef
{
    public override string ToString() => $"{Inner}?";
}

/// <summary>A value stored in a byte window of the given length (length-delimited sub-structure).</summary>
public sealed record SizedRef(TypeRef Inner, LengthSpec Length) : TypeRef
{
    public override string ToString() => $"sized({Inner}, {Length})";
}

/// <summary>A fractional number stored as a scaled integer: value = raw / 2^FractionBits.</summary>
public sealed record FixedPointRef(PrimitiveRef Base, int FractionBits) : TypeRef
{
    public override string ToString() => $"fixed({Base}, {FractionBits})";
}

public enum NbtFlavor
{
    /// <summary>Big endian, named root (Java files and pre-1.20.2 network).</summary>
    Java,
    /// <summary>Big endian, unnamed root (Java network since 1.20.2).</summary>
    JavaNetwork,
    /// <summary>Little endian, named root (Bedrock files).</summary>
    Bedrock,
    /// <summary>Little endian with varints for ints, longs and lengths, named root (Bedrock network).</summary>
    BedrockNetwork,
}

/// <summary>An NBT tag. <see cref="Flavor"/> is null when the project default applies.</summary>
public sealed record NbtRef(NbtFlavor? Flavor) : TypeRef
{
    public override string ToString() => $"nbt({Flavor?.ToString() ?? "default"})";
}
