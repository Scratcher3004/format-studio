using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Simulation;

namespace FormatStudio.Minecraft;

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

public enum NbtType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>
/// An NBT tag. <see cref="Value"/> is sbyte, short, int, long, float, double, byte[], string, <see cref="NbtList"/>,
/// <see cref="NbtCompound"/>, int[] or long[].
/// </summary>
public sealed record NbtTag(NbtType Type, object? Value);

public sealed class NbtList(NbtType elementType) : List<NbtTag>
{
    public NbtType ElementType { get; set; } = elementType;
}

public sealed class NbtCompound : List<KeyValuePair<string, NbtTag>>;

/// <summary>Binary NBT in the Java and Bedrock flavors, and SNBT text.</summary>
public static class Nbt
{
    private const int MaxDepth = 512;

    public static string FlavorName(NbtFlavor flavor) => flavor switch
    {
        NbtFlavor.Java => "java",
        NbtFlavor.JavaNetwork => "java-network",
        NbtFlavor.Bedrock => "bedrock",
        _ => "bedrock-network",
    };

    public static NbtFlavor ParseFlavor(string text) => text switch
    {
        "java" => NbtFlavor.Java,
        "java-network" => NbtFlavor.JavaNetwork,
        "bedrock" => NbtFlavor.Bedrock,
        "bedrock-network" => NbtFlavor.BedrockNetwork,
        _ => throw new System.FormatException($"Unknown NBT flavor '{text}' (java, java-network, bedrock, bedrock-network)"),
    };

    private static Endian EndianOf(NbtFlavor flavor) => flavor is NbtFlavor.Java or NbtFlavor.JavaNetwork ? Endian.Big : Endian.Little;

    /// <summary>Reads a root tag. Returns null for a lone TAG_End (no data).</summary>
    public static (string Name, NbtTag Tag)? Read(WireReader reader, NbtFlavor flavor)
    {
        var type = (NbtType)reader.ReadByte();
        if (type == NbtType.End) return null;
        if (type > NbtType.LongArray) throw reader.Error($"Unknown NBT tag type {(int)type}");
        var name = flavor == NbtFlavor.JavaNetwork ? "" : ReadString(reader, flavor);
        return (name, ReadPayload(reader, flavor, type, 0));
    }

    /// <summary>
    /// Reads an NBT value as JSON: the SNBT text, or <c>{name, snbt}</c> for a named root; null for an empty tag.
    /// </summary>
    public static JsonNode? ReadValue(INativeReader reader, NbtFlavor flavor)
    {
        var root = Read(reader.Wire, flavor);
        if (root == null)
        {
            reader.Display("empty");
            return null;
        }

        var snbt = ToSnbt(root.Value.Tag);
        reader.Display(snbt.Length <= 80 ? snbt : snbt[..77] + "...");
        return root.Value.Name.Length == 0
            ? JsonValue.Create(snbt)
            : new JsonObject { ["name"] = root.Value.Name, ["snbt"] = snbt };
    }

    /// <summary>Writes an NBT value given as by <see cref="ReadValue"/>.</summary>
    public static void WriteValue(INativeWriter writer, NbtFlavor flavor, JsonNode? value)
    {
        if (value == null)
        {
            Write(writer.Wire, flavor, "", null);
            return;
        }

        var (name, snbt) = value is JsonObject obj
            ? (JsonValues.String(obj["name"]) ?? "", JsonValues.String(obj["snbt"]) ?? throw writer.Error("NBT object needs 'snbt'"))
            : ("", JsonValues.String(value) ?? throw writer.Error($"Expected SNBT text, got {Codec.Show(value)}"));
        try
        {
            Write(writer.Wire, flavor, name, ParseSnbt(snbt));
        }
        catch (System.FormatException e)
        {
            throw writer.Error(e.Message);
        }
    }

    public static void Write(WireWriter writer, NbtFlavor flavor, string name, NbtTag? tag)
    {
        if (tag == null)
        {
            writer.WriteByte(0);
            return;
        }

        writer.WriteByte((byte)tag.Type);
        if (flavor != NbtFlavor.JavaNetwork) WriteString(writer, flavor, name);
        WritePayload(writer, flavor, tag);
    }

    private static int ReadLength(WireReader reader, NbtFlavor flavor)
    {
        var length = flavor == NbtFlavor.BedrockNetwork
            ? (int)reader.ReadInteger(PrimitiveKind.ZigZag32, Endian.Little)
            : (int)reader.ReadInteger(PrimitiveKind.I32, EndianOf(flavor));
        if (length < 0) throw reader.Error($"Negative NBT length {length}");
        if (length > reader.Remaining) throw reader.Error($"NBT length {length} exceeds the remaining {reader.Remaining} bytes");
        return length;
    }

    private static void WriteLength(WireWriter writer, NbtFlavor flavor, int length)
    {
        if (flavor == NbtFlavor.BedrockNetwork) writer.WriteInteger(PrimitiveKind.ZigZag32, Endian.Little, length);
        else writer.WriteInteger(PrimitiveKind.I32, EndianOf(flavor), length);
    }

    private static string ReadString(WireReader reader, NbtFlavor flavor)
    {
        var length = flavor == NbtFlavor.BedrockNetwork
            ? (int)reader.ReadInteger(PrimitiveKind.VarUInt32, Endian.Little)
            : (int)reader.ReadInteger(PrimitiveKind.U16, EndianOf(flavor));
        return reader.ReadString(length, flavor is NbtFlavor.Java or NbtFlavor.JavaNetwork ? StringEncoding.Mutf8 : StringEncoding.Utf8);
    }

    private static void WriteString(WireWriter writer, NbtFlavor flavor, string text)
    {
        var bytes = Strings.Encode(text, flavor is NbtFlavor.Java or NbtFlavor.JavaNetwork ? StringEncoding.Mutf8 : StringEncoding.Utf8);
        if (flavor == NbtFlavor.BedrockNetwork) writer.WriteInteger(PrimitiveKind.VarUInt32, Endian.Little, bytes.Length);
        else writer.WriteInteger(PrimitiveKind.U16, EndianOf(flavor), bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static NbtTag ReadPayload(WireReader reader, NbtFlavor flavor, NbtType type, int depth)
    {
        if (depth > MaxDepth) throw reader.Error("NBT nesting too deep");
        var endian = EndianOf(flavor);
        var network = flavor == NbtFlavor.BedrockNetwork;
        switch (type)
        {
            case NbtType.Byte: return new NbtTag(type, (sbyte)reader.ReadByte());
            case NbtType.Short: return new NbtTag(type, (short)reader.ReadInteger(PrimitiveKind.I16, endian));
            case NbtType.Int: return new NbtTag(type, (int)reader.ReadInteger(network ? PrimitiveKind.ZigZag32 : PrimitiveKind.I32, endian));
            case NbtType.Long: return new NbtTag(type, (long)reader.ReadInteger(network ? PrimitiveKind.ZigZag64 : PrimitiveKind.I64, endian));
            case NbtType.Float: return new NbtTag(type, (float)reader.ReadFloat(PrimitiveKind.F32, endian));
            case NbtType.Double: return new NbtTag(type, reader.ReadFloat(PrimitiveKind.F64, endian));
            case NbtType.ByteArray: return new NbtTag(type, reader.Take(ReadLength(reader, flavor)).ToArray());
            case NbtType.String: return new NbtTag(type, ReadString(reader, flavor));
            case NbtType.List:
                var elementType = (NbtType)reader.ReadByte();
                if (elementType > NbtType.LongArray) throw reader.Error($"Unknown NBT list element type {(int)elementType}");
                var count = ReadLength(reader, flavor);
                var list = new NbtList(elementType);
                for (var i = 0; i < count; i++) list.Add(ReadPayload(reader, flavor, elementType, depth + 1));
                return new NbtTag(type, list);
            case NbtType.Compound:
                var compound = new NbtCompound();
                while (true)
                {
                    var childType = (NbtType)reader.ReadByte();
                    if (childType == NbtType.End) break;
                    if (childType > NbtType.LongArray) throw reader.Error($"Unknown NBT tag type {(int)childType}");
                    var name = ReadString(reader, flavor);
                    compound.Add(new(name, ReadPayload(reader, flavor, childType, depth + 1)));
                }

                return new NbtTag(type, compound);
            case NbtType.IntArray:
                var ints = new int[ReadLength(reader, flavor)];
                for (var i = 0; i < ints.Length; i++)
                    ints[i] = (int)reader.ReadInteger(network ? PrimitiveKind.ZigZag32 : PrimitiveKind.I32, endian);
                return new NbtTag(type, ints);
            case NbtType.LongArray:
                var longs = new long[ReadLength(reader, flavor)];
                for (var i = 0; i < longs.Length; i++)
                    longs[i] = (long)reader.ReadInteger(network ? PrimitiveKind.ZigZag64 : PrimitiveKind.I64, endian);
                return new NbtTag(type, longs);
            default:
                throw reader.Error($"Unexpected NBT tag type {type}");
        }
    }

    private static void WritePayload(WireWriter writer, NbtFlavor flavor, NbtTag tag)
    {
        var endian = EndianOf(flavor);
        var network = flavor == NbtFlavor.BedrockNetwork;
        switch (tag.Value)
        {
            case sbyte b: writer.WriteByte((byte)b); break;
            case short s: writer.WriteInteger(PrimitiveKind.I16, endian, s); break;
            case int i: writer.WriteInteger(network ? PrimitiveKind.ZigZag32 : PrimitiveKind.I32, endian, i); break;
            case long l: writer.WriteInteger(network ? PrimitiveKind.ZigZag64 : PrimitiveKind.I64, endian, l); break;
            case float f: writer.WriteFloat(PrimitiveKind.F32, endian, f); break;
            case double d: writer.WriteFloat(PrimitiveKind.F64, endian, d); break;
            case byte[] bytes:
                WriteLength(writer, flavor, bytes.Length);
                writer.WriteBytes(bytes);
                break;
            case string text: WriteString(writer, flavor, text); break;
            case NbtList list:
                writer.WriteByte((byte)(list.Count == 0 ? list.ElementType : list[0].Type));
                WriteLength(writer, flavor, list.Count);
                foreach (var item in list)
                {
                    if (item.Type != list[0].Type) throw new EncodeException("NBT list elements must have the same type");
                    WritePayload(writer, flavor, item);
                }

                break;
            case NbtCompound compound:
                foreach (var (name, child) in compound)
                {
                    writer.WriteByte((byte)child.Type);
                    WriteString(writer, flavor, name);
                    WritePayload(writer, flavor, child);
                }

                writer.WriteByte(0);
                break;
            case int[] ints:
                WriteLength(writer, flavor, ints.Length);
                foreach (var i in ints) writer.WriteInteger(network ? PrimitiveKind.ZigZag32 : PrimitiveKind.I32, endian, i);
                break;
            case long[] longs:
                WriteLength(writer, flavor, longs.Length);
                foreach (var l in longs) writer.WriteInteger(network ? PrimitiveKind.ZigZag64 : PrimitiveKind.I64, endian, l);
                break;
            default:
                throw new EncodeException($"Unsupported NBT value {tag.Value}");
        }
    }

    // ---- SNBT ----

    public static string ToSnbt(NbtTag tag)
    {
        var builder = new StringBuilder();
        AppendSnbt(builder, tag);
        return builder.ToString();
    }

    private static void AppendSnbt(StringBuilder builder, NbtTag tag)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (tag.Value)
        {
            case sbyte b: builder.Append(b.ToString(inv)).Append('b'); break;
            case short s: builder.Append(s.ToString(inv)).Append('s'); break;
            case int i: builder.Append(i.ToString(inv)); break;
            case long l: builder.Append(l.ToString(inv)).Append('L'); break;
            case float f: builder.Append(FloatText(f.ToString("R", inv))).Append('f'); break;
            case double d: builder.Append(FloatText(d.ToString("R", inv))).Append('d'); break;
            case string text: AppendQuoted(builder, text); break;
            case byte[] bytes: builder.Append("[B;").AppendJoin(',', bytes.Select(b => ((sbyte)b).ToString(inv) + "b")).Append(']'); break;
            case int[] ints: builder.Append("[I;").AppendJoin(',', ints.Select(i => i.ToString(inv))).Append(']'); break;
            case long[] longs: builder.Append("[L;").AppendJoin(',', longs.Select(l => l.ToString(inv) + "L")).Append(']'); break;
            case NbtList list:
                builder.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    AppendSnbt(builder, list[i]);
                }

                builder.Append(']');
                break;
            case NbtCompound compound:
                builder.Append('{');
                for (var i = 0; i < compound.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    var key = compound[i].Key;
                    if (key.Length > 0 && key.All(IsUnquotedChar)) builder.Append(key);
                    else AppendQuoted(builder, key);
                    builder.Append(':');
                    AppendSnbt(builder, compound[i].Value);
                }

                builder.Append('}');
                break;
        }
    }

    private static string FloatText(string text) =>
        text.Contains('.') || text.Contains('E') || text.Contains("Infinity") || text.Contains("NaN") ? text : text + ".0";

    private static bool IsUnquotedChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+';

    private static void AppendQuoted(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            if (c is '"' or '\\') builder.Append('\\');
            builder.Append(c);
        }

        builder.Append('"');
    }

    public static NbtTag ParseSnbt(string text)
    {
        var parser = new SnbtParser(text);
        var tag = parser.ParseValue(0);
        parser.ExpectEnd();
        return tag;
    }

    private sealed class SnbtParser(string text)
    {
        private int _position;

        private System.FormatException Error(string message) => new($"SNBT: {message} at position {_position + 1}");

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position < text.Length) throw Error($"Unexpected '{text[_position]}'");
        }

        private bool Accept(char c)
        {
            SkipSpace();
            if (_position >= text.Length || text[_position] != c) return false;
            _position++;
            return true;
        }

        private void Expect(char c)
        {
            if (!Accept(c)) throw Error($"Expected '{c}'");
        }

        public NbtTag ParseValue(int depth)
        {
            if (depth > MaxDepth) throw Error("Nesting too deep");
            SkipSpace();
            if (_position >= text.Length) throw Error("Unexpected end");
            if (Accept('{')) return ParseCompound(depth);
            if (Accept('[')) return ParseListOrArray(depth);
            if (text[_position] is '"' or '\'') return new NbtTag(NbtType.String, ParseQuoted());
            return ParseScalar(ReadUnquoted());
        }

        private NbtTag ParseCompound(int depth)
        {
            var compound = new NbtCompound();
            if (Accept('}')) return new NbtTag(NbtType.Compound, compound);
            do
            {
                SkipSpace();
                var key = _position < text.Length && text[_position] is '"' or '\'' ? ParseQuoted() : ReadUnquoted();
                Expect(':');
                compound.Add(new(key, ParseValue(depth + 1)));
            } while (Accept(','));

            Expect('}');
            return new NbtTag(NbtType.Compound, compound);
        }

        private NbtTag ParseListOrArray(int depth)
        {
            SkipSpace();
            if (_position + 1 < text.Length && text[_position + 1] == ';' && text[_position] is 'B' or 'I' or 'L')
            {
                var kind = text[_position];
                _position += 2;
                var values = new List<NbtTag>();
                if (!Accept(']'))
                {
                    do values.Add(ParseScalar(ReadUnquoted()));
                    while (Accept(','));
                    Expect(']');
                }

                return kind switch
                {
                    'B' => new NbtTag(NbtType.ByteArray, values.Select(v => (byte)Convert.ToSByte(v.Value, CultureInfo.InvariantCulture)).ToArray()),
                    'I' => new NbtTag(NbtType.IntArray, values.Select(v => Convert.ToInt32(v.Value, CultureInfo.InvariantCulture)).ToArray()),
                    _ => new NbtTag(NbtType.LongArray, values.Select(v => Convert.ToInt64(v.Value, CultureInfo.InvariantCulture)).ToArray()),
                };
            }

            var list = new NbtList(NbtType.End);
            if (Accept(']')) return new NbtTag(NbtType.List, list);
            do list.Add(ParseValue(depth + 1));
            while (Accept(','));
            Expect(']');
            list.ElementType = list[0].Type;
            if (list.Any(item => item.Type != list.ElementType)) throw Error("List elements must have the same type");
            return new NbtTag(NbtType.List, list);
        }

        private string ParseQuoted()
        {
            var quote = text[_position++];
            var builder = new StringBuilder();
            while (_position < text.Length && text[_position] != quote)
            {
                if (text[_position] == '\\' && _position + 1 < text.Length) _position++;
                builder.Append(text[_position++]);
            }

            if (_position >= text.Length) throw Error("Unterminated string");
            _position++;
            return builder.ToString();
        }

        private string ReadUnquoted()
        {
            SkipSpace();
            var start = _position;
            while (_position < text.Length && IsUnquotedChar(text[_position])) _position++;
            if (start == _position) throw Error("Expected a value");
            return text[start.._position];
        }

        private static NbtTag ParseScalar(string token)
        {
            var inv = CultureInfo.InvariantCulture;
            if (token == "true") return new NbtTag(NbtType.Byte, (sbyte)1);
            if (token == "false") return new NbtTag(NbtType.Byte, (sbyte)0);
            var suffix = char.ToLowerInvariant(token[^1]);
            var body = token[..^1];
            const NumberStyles integer = NumberStyles.AllowLeadingSign;
            const NumberStyles real = NumberStyles.Float;
            switch (suffix)
            {
                case 'b' when sbyte.TryParse(body, integer, inv, out var b): return new NbtTag(NbtType.Byte, b);
                case 's' when short.TryParse(body, integer, inv, out var s): return new NbtTag(NbtType.Short, s);
                case 'l' when long.TryParse(body, integer, inv, out var l): return new NbtTag(NbtType.Long, l);
                case 'f' when float.TryParse(body, real, inv, out var f): return new NbtTag(NbtType.Float, f);
                case 'd' when double.TryParse(body, real, inv, out var d): return new NbtTag(NbtType.Double, d);
            }

            if (int.TryParse(token, integer, inv, out var i)) return new NbtTag(NbtType.Int, i);
            if (token.Contains('.') && double.TryParse(token, real, inv, out var dd)) return new NbtTag(NbtType.Double, dd);
            return new NbtTag(NbtType.String, token);
        }
    }
}
