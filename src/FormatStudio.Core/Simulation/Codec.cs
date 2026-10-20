using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

public sealed record DecodeResult(JsonNode? Value, SpanNode Root, DiagnosticBag Diagnostics, int Consumed)
{
    public bool Success => !Diagnostics.HasErrors;
}

public sealed record EncodeResult(byte[] Bytes, DiagnosticBag Diagnostics)
{
    public bool Success => !Diagnostics.HasErrors;
}

/// <summary>
/// Decodes bytes into JSON values and encodes JSON values into bytes, following the definitions of a project.
/// See the README for the JSON representation of each type.
/// </summary>
public sealed class Codec(FormatProject project)
{
    /// <summary>Upper bound for counts, so that a corrupt length doesn't allocate gigabytes.</summary>
    public int MaxCount { get; init; } = 1 << 24;

    public FormatProject Project => project;

    /// <summary>Parses a type name or type expression.</summary>
    public static TypeRef ParseType(string text) => TypeExpressions.Parse(text);

    public DecodeResult Decode(string type, byte[] data) => Decode(project.Normalize(ParseType(type)), data);

    public DecodeResult Decode(TypeRef type, byte[] data)
    {
        var diagnostics = new DiagnosticBag();
        var reader = new WireReader(data);
        var context = new ValueContext(project, evaluateComputed: false);
        var root = new SpanNode { Name = "$", Path = "$", Type = type.ToString(), Offset = 0 };
        JsonNode? value = null;
        try
        {
            var decoder = new Decoder(this, reader, context, diagnostics);
            value = decoder.Read(type, null, root, "$");
            root.Length = reader.Position;
            if (reader.Remaining > 0)
                diagnostics.Warning($"{reader.Remaining} bytes left after {type} at offset {reader.Position}", path: "$");
        }
        catch (DecodeException e)
        {
            root.Length = Math.Max(root.Length, reader.Position);
            diagnostics.Error($"At offset {e.Offset}: {e.Message}", path: e.Path);
        }
        catch (Exception e) when (e is ExprException or FormatDefinitionException or System.FormatException)
        {
            root.Length = Math.Max(root.Length, reader.Position);
            diagnostics.Error($"At offset {reader.Position}: {e.Message}");
        }

        return new DecodeResult(value, root, diagnostics, reader.Position);
    }

    public EncodeResult Encode(string type, JsonNode? value) => Encode(project.Normalize(ParseType(type)), value);

    public EncodeResult Encode(TypeRef type, JsonNode? value)
    {
        var diagnostics = new DiagnosticBag();
        var writer = new WireWriter();
        var context = new ValueContext(project, evaluateComputed: true);
        try
        {
            new Encoder(this, writer, context, diagnostics).Write(type, value, null, "$");
        }
        catch (EncodeException e)
        {
            diagnostics.Error(e.Message, path: e.Path);
        }
        catch (Exception e) when (e is ExprException or FormatDefinitionException or System.FormatException or InvalidOperationException)
        {
            diagnostics.Error(e.Message);
        }

        return new EncodeResult(writer.ToArray(), diagnostics);
    }

    internal Endian EndianOf(PrimitiveRef primitive) => primitive.Endian ?? project.Defaults.Endian;

    internal PrimitiveRef PrefixOf(LengthSpec length) => length.Prefix ?? project.Defaults.Length;

    internal static string Describe(TypeRef type) => TypeExpressions.TryFormat(type) ?? type.ToString();

    /// <summary>A value for error messages, shortened.</summary>
    internal static string Show(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "null";
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    private static string ChildPath(string path, string name) => path == "$" ? name : $"{path}.{name}";

    /// <summary>The struct whose fields the end condition of an <c>until</c> array uses.</summary>
    internal StructDef ElementStruct(ArrayRef array) =>
        project.Unalias(array.Element) is NamedRef named && project.Find(named.Name) is StructDef structDef
            ? structDef
            : throw new FormatDefinitionException($"An array with 'until' needs struct elements, not {Describe(array.Element)}");

    // ------------------------------------------------------------------ decoding

    private sealed class Decoder(Codec codec, WireReader reader, ValueContext context, DiagnosticBag diagnostics)
    {
        private FormatProject Project => codec.Project;
        private Codec Codec => codec;
        private ValueContext Context => context;
        private WireReader Reader => reader;
        private DiagnosticBag Diagnostics => diagnostics;

        /// <summary>The decoder as seen by the reading code of one native value.</summary>
        private sealed class NativeReader(Decoder decoder, StructScope? scope, SpanNode node, string path) : INativeReader
        {
            public FormatProject Project => decoder.Project;
            public WireReader Wire => decoder.Reader;
            public string Path => path;

            public void Display(string text) => node.Display = text;

            public JsonNode? Read(TypeRef type, string name)
            {
                var childPath = ChildPath(path, name);
                return decoder.Read(type, scope, decoder.Child(node, name, childPath, type), childPath);
            }

            public JsonNode? Decode(TypeRef type, byte[] data, string name)
            {
                var childPath = ChildPath(path, name);
                // The spans of the other bytes don't fit the input: the child spans none of it and only shows the value.
                var child = decoder.Child(node, name, childPath, type);
                var detached = new SpanNode { Name = name, Path = childPath, Type = child.Type, Offset = 0 };
                var inner = new WireReader(data);
                try
                {
                    var value = new Decoder(decoder.Codec, inner, decoder.Context, decoder.Diagnostics).Read(type, scope, detached, childPath);
                    if (inner.Remaining > 0) throw inner.Error($"{inner.Remaining} bytes left after {Describe(type)}");
                    child.Display = detached.Display;
                    return value;
                }
                catch (DecodeException e)
                {
                    child.Error = e.Message;
                    throw new DecodeException($"{name}, at offset {e.Offset} of its {data.Length} bytes: {e.Message}", decoder.Reader.Position, e.Path ?? childPath);
                }
            }

            public void Warning(string message) => decoder.Diagnostics.Warning(message, path: path);

            public DecodeException Error(string message) => decoder.Reader.Error(message);
        }

        public JsonNode? Read(TypeRef type, StructScope? scope, SpanNode node, string path)
        {
            try
            {
                return ReadInner(type, scope, node, path);
            }
            catch (DecodeException e) when (e.Path == null)
            {
                e.Path = path;
                node.Error = e.Message;
                throw;
            }
            catch (ExprException e)
            {
                node.Error = e.Message;
                throw new DecodeException(e.Message, reader.Position, path);
            }
            finally
            {
                node.Length = reader.Position - node.Offset;
            }
        }

        private SpanNode Child(SpanNode parent, string name, string path, TypeRef type)
        {
            var child = new SpanNode { Name = name, Path = path, Type = Describe(type), Offset = reader.Position };
            parent.Children.Add(child);
            return child;
        }

        private JsonNode? ReadInner(TypeRef type, StructScope? scope, SpanNode node, string path)
        {
            switch (type)
            {
                case PrimitiveRef primitive:
                    var primitiveValue = ReadPrimitive(primitive);
                    node.Display = primitiveValue?.ToJsonString();
                    return primitiveValue;
                case StringRef str:
                {
                    var encoding = str.Encoding ?? Project.Defaults.String;
                    string text;
                    if (str.Length.Kind == LengthKind.Zero)
                    {
                        var end = Array.IndexOf(reader.Data, (byte)0, reader.Position, reader.Remaining);
                        if (end < 0) throw reader.Error("Missing zero terminator");
                        text = ReadString(end - reader.Position, encoding);
                        reader.Position++;
                    }
                    else if (str.Padding != StringPadding.None)
                    {
                        var count = ReadCount(str.Length, scope, node, path, 1) ?? reader.Remaining;
                        var start = reader.Position;
                        text = ReadString(count, encoding, str.Padding, str.Pad);
                        var field = reader.Data.AsSpan(start, count);
                        var used = Strings.Unpad(field, str.Padding, str.Pad, encoding).Length;
                        var bad = Strings.BadPadding(field, str.Padding, str.Pad, encoding);
                        if (bad >= 0)
                        {
                            const string why = "after the terminator: the string would re-encode with zeros";
                            switch (str.Check ?? Project.Defaults.Check)
                            {
                                case PaddingCheck.Strict:
                                    throw new DecodeException($"Padding byte 0x{field[bad]:x2} {why}", start + bad, path);
                                case PaddingCheck.Warn:
                                    diagnostics.Warning($"Padding byte 0x{field[bad]:x2} at offset {start + bad} {why}", path: path);
                                    break;
                            }
                        }

                        if (used < count)
                        {
                            node.Children.Add(new SpanNode
                            {
                                Name = "padding", Path = path + ".padding", Type = "padding", Offset = start + used, Length = count - used,
                                Display = Hex.Format(reader.Data.AsSpan(start + used, Math.Min(count - used, 16))) + (count - used > 16 ? "…" : ""),
                            });
                        }
                    }
                    else
                    {
                        text = ReadString(ReadCount(str.Length, scope, node, path, 1) ?? reader.Remaining, encoding);
                    }

                    node.Display = JsonSerializer.Serialize(text);
                    return JsonValue.Create(text);
                }
                case BytesRef bytes:
                {
                    var count = ReadCount(bytes.Length, scope, node, path, 1) ?? reader.Remaining;
                    var hex = Hex.Format(reader.Take(count));
                    node.Display = count <= 32 ? hex : $"{count} bytes";
                    return JsonValue.Create(hex);
                }
                case ArrayRef { Length.Kind: LengthKind.Until } until:
                {
                    var condition = Expr.Parse(until.Length.Expression!);
                    var elementDef = codec.ElementStruct(until);
                    var items = new JsonArray();
                    while (true)
                    {
                        if (reader.Remaining == 0) throw reader.Error($"The data ends before an element where {until.Length.Expression}");
                        if (items.Count >= codec.MaxCount) throw reader.Error($"More than {codec.MaxCount} elements");
                        var itemPath = $"{path}[{items.Count}]";
                        var item = Read(until.Element, scope, Child(node, $"[{items.Count}]", itemPath, until.Element), itemPath);
                        items.Add(item);
                        if (item is JsonObject obj && context.Evaluator.EvaluateBool(condition, new StructScope(context, elementDef, obj, scope))) break;
                    }

                    node.Display = $"{items.Count} items";
                    return items;
                }
                case ArrayRef array:
                {
                    var count = ReadCount(array.Length, scope, node, path, MinSize(array.Element));
                    var items = new JsonArray();
                    for (var i = 0; count == null ? reader.Remaining > 0 : i < count; i++)
                    {
                        var itemPath = $"{path}[{i}]";
                        items.Add(Read(array.Element, scope, Child(node, $"[{i}]", itemPath, array.Element), itemPath));
                    }

                    node.Display = $"{items.Count} items";
                    return items;
                }
                case MapRef map:
                    return ReadMap(map, scope, node, path);
                case OptionalRef optional:
                {
                    var present = reader.ReadByte();
                    if (present > 1) throw reader.Error($"Presence byte must be 0 or 1, got {present}");
                    if (present == 0)
                    {
                        node.Display = "absent";
                        return null;
                    }

                    var innerPath = path;
                    return Read(optional.Inner, scope, Child(node, "value", innerPath, optional.Inner), innerPath);
                }
                case SizedRef sized:
                {
                    var count = ReadCount(sized.Length, scope, node, path, 0) ?? reader.Remaining;
                    reader.PushLimit(count);
                    try
                    {
                        var value = Read(sized.Inner, scope, Child(node, "content", path, sized.Inner), path);
                        if (sized.Fill != null)
                        {
                            ReadFill(sized, node, path);
                        }
                        else if (reader.Remaining > 0)
                        {
                            diagnostics.Warning($"{reader.Remaining} unread bytes in the sized section", path: path);
                            reader.Take(reader.Remaining);
                        }

                        return value;
                    }
                    finally
                    {
                        reader.PopLimit();
                    }
                }
                case FixedPointRef fixedPoint:
                {
                    var raw = reader.ReadInteger(fixedPoint.Base.Kind, codec.EndianOf(fixedPoint.Base));
                    var value = (double)raw / Math.Pow(2, fixedPoint.FractionBits);
                    node.Display = value.ToString("R", CultureInfo.InvariantCulture);
                    return JsonValues.FromDouble(value);
                }
                case NativeRef native:
                {
                    var bound = Project.Bind(native);
                    return bound.Type.Read(new NativeReader(this, scope, node, path), bound.Args);
                }
                case NamedRef named:
                    return ReadNamed(named, scope, node, path);
                default:
                    throw reader.Error($"Unsupported type {type}");
            }
        }

        /// <summary>The rest of a sized region with a fill: checked against the pattern and shown as a padding node.</summary>
        private void ReadFill(SizedRef sized, SpanNode node, string path)
        {
            if (reader.Remaining == 0) return;
            var start = reader.Position;
            var rest = reader.Take(reader.Remaining);
            var pattern = Convert.FromHexString(sized.Fill!);
            node.Children.Add(new SpanNode
            {
                Name = "padding", Path = path + ".padding", Type = "fill " + TypeExpressions.FormatFill(sized.Fill!), Offset = start, Length = rest.Length,
                Display = Hex.Format(rest[..Math.Min(rest.Length, 16)]) + (rest.Length > 16 ? "\u2026" : ""),
            });
            var bad = -1;
            for (var i = 0; i < rest.Length && bad < 0; i++)
                if (rest[i] != pattern[i % pattern.Length]) bad = i;
            if (bad < 0) return;
            var why = $"in the fill: the region would re-encode with {TypeExpressions.FormatFill(sized.Fill!)}";
            switch (sized.Check ?? Project.Defaults.Check)
            {
                case PaddingCheck.Strict:
                    throw new DecodeException($"Byte 0x{rest[bad]:x2} {why}", start + bad, path);
                case PaddingCheck.Warn:
                    diagnostics.Warning($"Byte 0x{rest[bad]:x2} at offset {start + bad} {why}", path: path);
                    break;
            }
        }

        private string ReadString(int count, StringEncoding encoding, StringPadding padding = StringPadding.None, char pad = '\0')
        {
            var start = reader.Position;
            try
            {
                return reader.ReadString(count, encoding, padding, pad);
            }
            catch (DecodeException e)
            {
                throw new DecodeException(e.Message, start);
            }
        }

        private int MinSize(TypeRef type) => Project.Unalias(type) switch
        {
            PrimitiveRef primitive => Primitives.MinWireSize(primitive.Kind),
            FixedPointRef fixedPoint => Primitives.MinWireSize(fixedPoint.Base.Kind),
            NamedRef { As: { } asType } => Primitives.MinWireSize(asType.Kind),
            NamedRef named when Project.Find(named.Name) is EnumDef enumDef => Primitives.MinWireSize(enumDef.Type.Kind),
            _ => 0,
        };

        private JsonNode? ReadPrimitive(PrimitiveRef primitive)
        {
            var endian = codec.EndianOf(primitive);
            switch (primitive.Kind)
            {
                case PrimitiveKind.Bool:
                    var b = reader.ReadByte();
                    if (b > 1) diagnostics.Warning($"Boolean byte 0x{b:x2} at offset {reader.Position - 1}");
                    return JsonValue.Create(b != 0);
                case PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64:
                    return JsonValues.FromDouble(reader.ReadFloat(primitive.Kind, endian));
                case PrimitiveKind.Uuid:
                    return JsonValue.Create(reader.ReadUuid(endian));
                default:
                    return JsonValues.FromInteger(reader.ReadInteger(primitive.Kind, endian));
            }
        }

        /// <summary>Reads or evaluates an element count. Null means "until the end".</summary>
        private int? ReadCount(LengthSpec length, StructScope? scope, SpanNode node, string path, int minElementSize)
        {
            long count;
            switch (length.Kind)
            {
                case LengthKind.Default or LengthKind.Prefix:
                    var prefix = codec.PrefixOf(length);
                    var prefixNode = new SpanNode { Name = "length", Path = path + ".length", Type = prefix.ToString(), Offset = reader.Position };
                    node.Children.Add(prefixNode);
                    count = (long)reader.ReadInteger(prefix.Kind, codec.EndianOf(prefix));
                    prefixNode.Length = reader.Position - prefixNode.Offset;
                    prefixNode.Display = count.ToString(CultureInfo.InvariantCulture);
                    break;
                case LengthKind.Fixed:
                    count = length.Fixed;
                    break;
                case LengthKind.Expression:
                    if (scope == null) throw reader.Error($"Length expression '{length.Expression}' outside a struct");
                    count = context.Evaluator.EvaluateLong(Expr.Parse(length.Expression!), scope);
                    break;
                default:
                    return null;
            }

            if (count < 0) throw reader.Error($"Negative length {count}");
            if (count > codec.MaxCount) throw reader.Error($"Length {count} exceeds the limit of {codec.MaxCount}");
            if (count * minElementSize > reader.Remaining)
                throw reader.Error($"Length {count} needs at least {count * minElementSize} bytes, {reader.Remaining} remaining");
            return (int)count;
        }

        private JsonNode ReadMap(MapRef map, StructScope? scope, SpanNode node, string path)
        {
            var count = ReadCount(map.Length, scope, node, path, MinSize(map.Key));
            var stringKeys = Project.Unalias(map.Key) is StringRef;
            var entries = new List<(JsonNode? Key, JsonNode? Value)>();
            for (var i = 0; count == null ? reader.Remaining > 0 : i < count; i++)
            {
                var entryPath = $"{path}[{i}]";
                var key = Read(map.Key, scope, Child(node, $"[{i}].key", entryPath + ".key", map.Key), entryPath + ".key");
                var value = Read(map.Value, scope, Child(node, $"[{i}].value", entryPath + ".value", map.Value), entryPath + ".value");
                entries.Add((key, value));
            }

            node.Display = $"{entries.Count} entries";
            if (stringKeys && entries.Select(e => JsonValues.String(e.Key)).Distinct().Count() == entries.Count)
            {
                var obj = new JsonObject();
                foreach (var (key, value) in entries) obj[JsonValues.String(key)!] = value;
                return obj;
            }

            return new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["key"] = e.Key, ["value"] = e.Value }).ToArray());
        }

        private JsonNode? ReadNamed(NamedRef named, StructScope? scope, SpanNode node, string path)
        {
            var definition = Project.Find(named.Name) ?? throw reader.Error($"Unknown type '{named.Name}'");
            switch (definition)
            {
                case AliasDef alias:
                    return ReadInner(alias.Target, scope, node, path);
                case StructDef structDef:
                {
                    var obj = new JsonObject();
                    var structScope = new StructScope(context, structDef, obj, scope);
                    foreach (var field in structDef.Fields)
                    {
                        var fieldPath = path == "$" ? field.Name : $"{path}.{field.Name}";
                        if (field.If != null && !context.Evaluator.EvaluateBool(Expr.Parse(field.If), structScope)) continue;
                        var value = Read(field.Type, structScope, Child(node, field.Name, fieldPath, field.Type), fieldPath);
                        if (field.Const != null && !JsonValues.DeepEquals(field.Const, value))
                            diagnostics.Warning($"Expected const {field.Const.ToJsonString()}, got {value?.ToJsonString() ?? "null"}", path: fieldPath);
                        CheckConstraints(field, value, fieldPath);
                        obj[field.Name] = value;
                    }

                    return obj;
                }
                case EnumDef enumDef:
                {
                    var wire = named.As ?? enumDef.Type;
                    var raw = (long)reader.ReadInteger(wire.Kind, codec.EndianOf(wire));
                    var result = EnumToJson(enumDef, raw, path);
                    node.Display = $"{result} ({raw})";
                    return result;
                }
                case VariantDef variant:
                {
                    object? tag;
                    if (named.Tag != null)
                    {
                        if (scope == null) throw reader.Error("External variant tag outside a struct");
                        tag = context.Evaluator.Evaluate(Expr.Parse(named.Tag), scope);
                    }
                    else
                    {
                        var tagNode = Child(node, "tag", path + ".tag", variant.Tag!);
                        var tagValue = Read(variant.Tag!, scope, tagNode, path + ".tag");
                        tag = Project.TagKind(variant) switch
                        {
                            VariantTagKind.String => JsonValues.String(tagValue),
                            VariantTagKind.Enum => context.EnumValue((EnumDef)Project.Find(((NamedRef)variant.Tag!).Name)!, tagValue)
                                ?? throw reader.Error($"Unknown tag {tagValue}"),
                            _ => JsonValues.Number(tagValue),
                        };
                    }

                    var variantCase = context.FindCase(variant, tag)
                        ?? throw reader.Error($"{variant.Name} has no case with tag {(tag is string text ? $"'{text}'" : tag)}");
                    node.Display = variantCase.Name;
                    if (variantCase.Type == null) return new JsonObject { [variantCase.Name] = null };
                    var casePath = $"{path}.{variantCase.Name}";
                    var caseValue = Read(variantCase.Type, scope, Child(node, variantCase.Name, casePath, variantCase.Type), casePath);
                    return new JsonObject { [variantCase.Name] = caseValue };
                }
                case BitfieldDef bitfield:
                {
                    var raw = reader.ReadInteger(bitfield.Base.Kind, codec.EndianOf(bitfield.Base));
                    var bits = unchecked(raw < 0 ? (ulong)(long)raw : (ulong)raw);
                    var obj = new JsonObject();
                    foreach (var (member, shift) in Bitfields.Layout(bitfield))
                    {
                        var mask = member.Bits == 64 ? ulong.MaxValue : (1UL << member.Bits) - 1;
                        var value = (bits >> shift) & mask;
                        decimal number = member.Signed && member.Bits < 64 && (value >> (member.Bits - 1)) != 0
                            ? (long)value - (1L << member.Bits)
                            : member.Signed ? (long)value : value;
                        obj[member.Name] = member.Type switch
                        {
                            null => JsonValues.FromInteger(number),
                            "bool" => JsonValue.Create(number != 0),
                            var enumName => Project.Find(enumName) is EnumDef e ? EnumToJson(e, (long)number, $"{path}.{member.Name}") : null,
                        };
                    }

                    node.Display = obj.ToJsonString();
                    return obj;
                }
                default:
                    throw reader.Error($"Unsupported definition {definition.Kind}");
            }
        }

        private JsonNode EnumToJson(EnumDef enumDef, long raw, string path)
        {
            if (enumDef.FindByValue(raw) is { } member) return JsonValue.Create(member.Name);
            if (enumDef.Flags)
            {
                var names = enumDef.Members.Where(m => m.Value != 0 && (raw & m.Value) == m.Value).ToList();
                var covered = names.Aggregate(0L, (acc, m) => acc | m.Value);
                if (covered == raw && names.Count > 0) return JsonValue.Create(string.Join(" | ", names.Select(m => m.Name)));
            }

            if (!enumDef.AllowUnknown) diagnostics.Warning($"{raw} is not a member of {enumDef.Name}", path: path);
            return JsonValue.Create(raw);
        }

        private void CheckConstraints(FieldDef field, JsonNode? value, string path)
        {
            if (value == null) return;
            if ((field.Min != null || field.Max != null) && JsonValues.Number(value) is { } number && value.GetValueKind() == JsonValueKind.Number)
            {
                if (number < field.Min || number > field.Max)
                    diagnostics.Warning($"{number} is outside [{field.Min}, {field.Max}]", path: path);
            }

            if (field.MaxLength != null && JsonValues.String(value) is { } text && text.Length > field.MaxLength)
                diagnostics.Warning($"Length {text.Length} exceeds maxLength {field.MaxLength}", path: path);
            if (field.MaxItems != null && value is JsonArray array && array.Count > field.MaxItems)
                diagnostics.Warning($"{array.Count} items exceed maxItems {field.MaxItems}", path: path);
        }
    }

    // ------------------------------------------------------------------ encoding

    private sealed class Encoder(Codec codec, WireWriter writer, ValueContext context, DiagnosticBag diagnostics)
    {
        private FormatProject Project => codec.Project;
        private Codec Codec => codec;
        private ValueContext Context => context;
        private WireWriter Writer => writer;
        private DiagnosticBag Diagnostics => diagnostics;

        /// <summary>The encoder as seen by the writing code of one native value.</summary>
        private sealed class NativeWriter(Encoder encoder, StructScope? scope, string path) : INativeWriter
        {
            public FormatProject Project => encoder.Project;
            public WireWriter Wire => encoder.Writer;
            public string Path => path;

            public void Write(TypeRef type, JsonNode? value, string name) => encoder.Write(type, value, scope, ChildPath(path, name));

            public byte[] Encode(TypeRef type, JsonNode? value, string name)
            {
                var inner = new WireWriter();
                new Encoder(encoder.Codec, inner, encoder.Context, encoder.Diagnostics).Write(type, value, scope, ChildPath(path, name));
                return inner.ToArray();
            }

            public void Warning(string message) => encoder.Diagnostics.Warning(message, path: path);

            public EncodeException Error(string message) => new(message);
        }

        public void Write(TypeRef type, JsonNode? value, StructScope? scope, string path)
        {
            try
            {
                WriteInner(type, value, scope, path);
            }
            catch (EncodeException e) when (e.Path == null)
            {
                e.Path = path;
                throw;
            }
            catch (ExprException e)
            {
                throw new EncodeException(e.Message, path);
            }
        }

        private static EncodeException Error(string message) => new(message);

        private void WriteInner(TypeRef type, JsonNode? value, StructScope? scope, string path)
        {
            switch (type)
            {
                case PrimitiveRef primitive:
                    WritePrimitive(primitive, value);
                    break;
                case StringRef str:
                {
                    var text = JsonValues.String(value) ?? throw Error($"Expected a string, got {Show(value)}");
                    var encoding = str.Encoding ?? Project.Defaults.String;
                    byte[] bytes;
                    try
                    {
                        bytes = Strings.Encode(text, encoding);
                    }
                    catch (System.Text.EncoderFallbackException e)
                    {
                        throw Error($"{JsonSerializer.Serialize(e.CharUnknown.ToString())} can't be written as {TypeExpressions.EncodingName(encoding)}");
                    }

                    if (str.Padding != StringPadding.None)
                        WritePadded(str, text, bytes, encoding, scope, path);
                    else if (str.Length.Kind == LengthKind.Zero)
                    {
                        if (bytes.Contains((byte)0)) throw Error("A zero-terminated string can't contain NUL");
                        writer.WriteBytes(bytes);
                        writer.WriteByte(0);
                    }
                    else
                    {
                        WriteCount(str.Length, bytes.Length, scope);
                        writer.WriteBytes(bytes);
                    }

                    break;
                }
                case BytesRef bytesRef:
                {
                    var hex = JsonValues.String(value) ?? throw Error($"Expected a hex string, got {Show(value)}");
                    byte[] bytes;
                    try
                    {
                        bytes = Hex.Parse(hex);
                    }
                    catch (System.FormatException e)
                    {
                        throw Error(e.Message);
                    }

                    WriteCount(bytesRef.Length, bytes.Length, scope);
                    writer.WriteBytes(bytes);
                    break;
                }
                case ArrayRef { Length.Kind: LengthKind.Until } until:
                {
                    var items = value as JsonArray ?? throw Error($"Expected an array, got {Show(value)}");
                    if (items.Count == 0) throw Error($"The array needs at least its last element, where {until.Length.Expression}");
                    var condition = Expr.Parse(until.Length.Expression!);
                    var elementDef = codec.ElementStruct(until);
                    for (var i = 0; i < items.Count; i++)
                    {
                        var itemPath = $"{path}[{i}]";
                        Write(until.Element, items[i], scope, itemPath);
                        var ends = items[i] is JsonObject obj && context.Evaluator.EvaluateBool(condition, new StructScope(context, elementDef, obj, scope));
                        if (ends && i < items.Count - 1)
                            throw new EncodeException($"{until.Length.Expression} is true here, so a reader stops after this element", itemPath);
                        if (!ends && i == items.Count - 1)
                            throw new EncodeException($"The last element must end the array: {until.Length.Expression} is false", itemPath);
                    }

                    break;
                }
                case ArrayRef array:
                {
                    var items = value as JsonArray ?? throw Error($"Expected an array, got {Show(value)}");
                    WriteCount(array.Length, items.Count, scope);
                    for (var i = 0; i < items.Count; i++) Write(array.Element, items[i], scope, $"{path}[{i}]");
                    break;
                }
                case MapRef map:
                {
                    var entries = value switch
                    {
                        JsonObject obj => obj.Select(pair => ((JsonNode?)JsonValue.Create(pair.Key), pair.Value)).ToList(),
                        JsonArray array => array.Select(entry => entry is JsonObject e
                            ? (e["key"], e["value"])
                            : throw Error("Map entries must be {key, value} objects")).ToList(),
                        _ => throw Error($"Expected an object or an array of {{key, value}}, got {Show(value)}"),
                    };
                    WriteCount(map.Length, entries.Count, scope);
                    for (var i = 0; i < entries.Count; i++)
                    {
                        Write(map.Key, entries[i].Item1, scope, $"{path}[{i}].key");
                        Write(map.Value, entries[i].Item2, scope, $"{path}[{i}].value");
                    }

                    break;
                }
                case OptionalRef optional:
                    if (value == null)
                    {
                        writer.WriteByte(0);
                    }
                    else
                    {
                        writer.WriteByte(1);
                        Write(optional.Inner, value, scope, path);
                    }

                    break;
                case SizedRef sized:
                {
                    var inner = new WireWriter();
                    new Encoder(codec, inner, context, diagnostics).Write(sized.Inner, value, scope, path);
                    if (sized.Fill != null)
                    {
                        var size = FieldSize(sized.Length, scope);
                        if (inner.Length > size) throw Error($"The value takes {inner.Length} bytes, more than the {size} of its region");
                        writer.WriteBytes(inner.Written);
                        var pattern = Convert.FromHexString(sized.Fill);
                        for (var i = 0; i < size - inner.Length; i++) writer.WriteByte(pattern[i % pattern.Length]);
                        break;
                    }

                    WriteCount(sized.Length, inner.Length, scope);
                    writer.WriteBytes(inner.Written);
                    break;
                }
                case FixedPointRef fixedPoint:
                {
                    var number = JsonValues.Double(value) ?? throw Error($"Expected a number, got {Show(value)}");
                    var raw = Math.Round(number * Math.Pow(2, fixedPoint.FractionBits));
                    writer.WriteInteger(fixedPoint.Base.Kind, codec.EndianOf(fixedPoint.Base), (decimal)raw);
                    break;
                }
                case NativeRef native:
                {
                    var bound = Project.Bind(native);
                    bound.Type.Write(new NativeWriter(this, scope, path), bound.Args, value);
                    break;
                }
                case NamedRef named:
                    WriteNamed(named, value, scope, path);
                    break;
                default:
                    throw Error($"Unsupported type {type}");
            }
        }

        private void WritePrimitive(PrimitiveRef primitive, JsonNode? value)
        {
            var endian = codec.EndianOf(primitive);
            switch (primitive.Kind)
            {
                case PrimitiveKind.Bool:
                    writer.WriteByte(value?.GetValueKind() switch
                    {
                        JsonValueKind.True => 1,
                        JsonValueKind.False => 0,
                        _ => JsonValues.Number(value) is { } n and (0 or 1) ? (byte)n : throw Error($"Expected a boolean, got {Show(value)}"),
                    });
                    break;
                case PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64:
                    writer.WriteFloat(primitive.Kind, endian, JsonValues.Double(value) ?? throw Error($"Expected a number, got {Show(value)}"));
                    break;
                case PrimitiveKind.Uuid:
                    writer.WriteUuid(endian, JsonValues.String(value) ?? throw Error($"Expected a UUID string, got {Show(value)}"));
                    break;
                default:
                    writer.WriteInteger(primitive.Kind, endian, JsonValues.Number(value) ?? throw Error($"Expected an integer, got {Show(value)}"));
                    break;
            }
        }

        /// <summary>A string in a field of a fixed or computed number of bytes, followed by its padding.</summary>
        private void WritePadded(StringRef str, string text, byte[] bytes, StringEncoding encoding, StructScope? scope, string path)
        {
            var size = FieldSize(str.Length, scope);
            if (bytes.Length > size) throw Error($"The string takes {bytes.Length} bytes, more than the {size} of its field");
            if (str.Padding == StringPadding.Terminated && text.Contains('\0')) throw Error("A NUL-terminated string can't contain NUL");
            if (str.Padding == StringPadding.Padded && text.EndsWith(str.Pad) && (str.Check ?? Project.Defaults.Check) == PaddingCheck.Strict)
                throw Error($"The string ends with its pad character {JsonSerializer.Serialize(str.Pad.ToString())}, which would be dropped when read");
            if (str.Padding == StringPadding.Padded && text.EndsWith(str.Pad))
                diagnostics.Warning($"The string ends with its pad character {JsonSerializer.Serialize(str.Pad.ToString())}, which is dropped when read", path: path);
            writer.WriteBytes(bytes);
            var unit = Strings.PadUnit(str.Pad, encoding);
            var rest = size - bytes.Length;
            for (; rest >= unit.Length; rest -= unit.Length) writer.WriteBytes(unit);
            for (; rest > 0; rest--) writer.WriteByte(0);
        }

        /// <summary>The byte size of a padded field: a fixed length or a length expression, within the limit.</summary>
        private int FieldSize(LengthSpec length, StructScope? scope)
        {
            long size;
            switch (length.Kind)
            {
                case LengthKind.Fixed:
                    size = length.Fixed;
                    break;
                case LengthKind.Expression:
                    if (scope == null) throw Error($"Length expression '{length.Expression}' outside a struct");
                    size = context.Evaluator.EvaluateLong(Expr.Parse(length.Expression!), scope);
                    break;
                default:
                    throw Error($"Padding needs a fixed length or a length expression, not {length}");
            }

            if (size < 0) throw Error($"Negative length {size}");
            if (size > codec.MaxCount) throw Error($"Length {size} exceeds the limit of {codec.MaxCount}");
            return (int)size;
        }

        private void WriteCount(LengthSpec length, int count, StructScope? scope)
        {
            switch (length.Kind)
            {
                case LengthKind.Default or LengthKind.Prefix:
                    var prefix = codec.PrefixOf(length);
                    writer.WriteInteger(prefix.Kind, codec.EndianOf(prefix), count);
                    break;
                case LengthKind.Fixed when count != length.Fixed:
                    throw Error($"Expected exactly {length.Fixed} elements, got {count}");
                case LengthKind.Expression:
                    if (scope == null) throw Error($"Length expression '{length.Expression}' outside a struct");
                    var expected = context.Evaluator.EvaluateLong(Expr.Parse(length.Expression!), scope);
                    if (expected != count) throw Error($"The length expression '{length.Expression}' is {expected}, but there are {count} elements");
                    break;
            }
        }

        private void WriteNamed(NamedRef named, JsonNode? value, StructScope? scope, string path)
        {
            var definition = Project.Find(named.Name) ?? throw Error($"Unknown type '{named.Name}'");
            switch (definition)
            {
                case AliasDef alias:
                    WriteInner(alias.Target, value, scope, path);
                    break;
                case StructDef structDef:
                {
                    var obj = value as JsonObject ?? throw Error($"Expected an object for {structDef.Name}, got {Show(value)}");
                    var structScope = new StructScope(context, structDef, obj, scope);
                    foreach (var key in obj.Select(pair => pair.Key).Where(key => structDef.Fields.All(f => f.Name != key)))
                        diagnostics.Warning($"Unknown field '{key}' in {structDef.Name}", path: path);
                    foreach (var field in structDef.Fields)
                    {
                        var fieldPath = path == "$" ? field.Name : $"{path}.{field.Name}";
                        try
                        {
                            if (field.If != null && !context.Evaluator.EvaluateBool(Expr.Parse(field.If), structScope))
                            {
                                if (obj[field.Name] != null && field.Computed == null)
                                    diagnostics.Warning("The value is ignored because the condition is false", path: fieldPath);
                                continue;
                            }

                            var fieldValue = field.Computed != null
                                ? FromExprValue(context.Evaluator.Evaluate(Expr.Parse(field.Computed), structScope), field.Type)
                                : obj.ContainsKey(field.Name) ? obj[field.Name] : field.Const ?? field.Default;
                            if (field.Const != null && obj[field.Name] is { } given && !JsonValues.DeepEquals(given, field.Const))
                                diagnostics.Warning($"Replaced by const {field.Const.ToJsonString()}", path: fieldPath);
                            if (field.Const != null) fieldValue = field.Const;
                            if (fieldValue == null && !obj.ContainsKey(field.Name) && !IsNullable(field.Type))
                                throw new EncodeException($"Missing field '{field.Name}'", fieldPath);
                            Write(field.Type, fieldValue, structScope, fieldPath);
                        }
                        catch (ExprException e)
                        {
                            throw new EncodeException(e.Message, fieldPath);
                        }
                    }

                    break;
                }
                case EnumDef enumDef:
                {
                    var wire = named.As ?? enumDef.Type;
                    var number = context.EnumValue(enumDef, value)
                        ?? throw Error($"{Show(value)} is not a member of {enumDef.Name}");
                    if (JsonValues.String(value) == null && enumDef.FindByValue((long)number) == null && !enumDef.AllowUnknown && !enumDef.Flags)
                        diagnostics.Warning($"{number} is not a member of {enumDef.Name}", path: path);
                    writer.WriteInteger(wire.Kind, codec.EndianOf(wire), number);
                    break;
                }
                case VariantDef variant:
                {
                    var (caseName, caseValue) = value switch
                    {
                        JsonObject { Count: 1 } obj => (obj.First().Key, obj.First().Value),
                        JsonValue when JsonValues.String(value) is { } bare => (bare, null),
                        _ => throw Error($"Expected {{CaseName: value}} for {variant.Name}, got {Show(value)}"),
                    };
                    var variantCase = variant.Cases.FirstOrDefault(c => c.Name == caseName)
                        ?? throw Error($"{variant.Name} has no case '{caseName}' ({string.Join(", ", variant.Cases.Select(c => c.Name))})");
                    var tag = context.CaseTag(variant, variantCase) ?? throw Error($"Can't resolve the tag of {variantCase.Name}");
                    if (named.Tag != null)
                    {
                        if (scope == null) throw Error("External variant tag outside a struct");
                        var expected = ValueContext.NormalizeTag(context.Evaluator.Evaluate(Expr.Parse(named.Tag), scope));
                        if (!Equals(expected, tag)) throw Error($"The tag expression '{named.Tag}' is {expected ?? "null"}, but case {caseName} has tag {tag}");
                    }
                    else if (tag is string text)
                    {
                        Write(variant.Tag!, JsonValue.Create(text), scope, path + ".tag");
                    }
                    else if (variant.Tag is NamedRef tagRef && Project.Find(tagRef.Name) is EnumDef tagEnum)
                    {
                        var wire = tagRef.As ?? tagEnum.Type;
                        writer.WriteInteger(wire.Kind, codec.EndianOf(wire), (long)tag);
                    }
                    else if (Project.Unalias(variant.Tag!) is PrimitiveRef tagPrimitive)
                    {
                        writer.WriteInteger(tagPrimitive.Kind, codec.EndianOf(tagPrimitive), (long)tag);
                    }

                    if (variantCase.Type != null) Write(variantCase.Type, caseValue, scope, $"{path}.{caseName}");
                    break;
                }
                case BitfieldDef bitfield:
                {
                    var obj = value as JsonObject ?? throw Error($"Expected an object for {bitfield.Name}, got {Show(value)}");
                    ulong bits = 0;
                    foreach (var (member, shift) in Bitfields.Layout(bitfield))
                    {
                        var memberValue = obj[member.Name];
                        decimal number = member.Type switch
                        {
                            null => JsonValues.Number(memberValue) ?? 0,
                            "bool" => memberValue?.GetValueKind() == JsonValueKind.True ? 1 : JsonValues.Number(memberValue) ?? 0,
                            var enumName => Project.Find(enumName) is EnumDef e ? context.EnumValue(e, memberValue) ?? 0 : 0,
                        };
                        var min = member.Signed ? -(decimal)Math.Pow(2, member.Bits - 1) : 0;
                        var max = member.Signed ? (decimal)Math.Pow(2, member.Bits - 1) - 1 : (decimal)Math.Pow(2, member.Bits) - 1;
                        if (number < min || number > max) throw new EncodeException($"{number} doesn't fit {member.Bits} bits", $"{path}.{member.Name}");
                        var mask = member.Bits == 64 ? ulong.MaxValue : (1UL << member.Bits) - 1;
                        bits |= (unchecked((ulong)(long)number) & mask) << shift;
                    }

                    var raw = Primitives.IsSigned(bitfield.Base.Kind) ? (decimal)unchecked((long)bits) : bits;
                    if (Primitives.IsSigned(bitfield.Base.Kind) && Primitives.Bits(bitfield.Base.Kind) < 64)
                    {
                        var width = Primitives.Bits(bitfield.Base.Kind);
                        if ((bits >> (width - 1) & 1) != 0) raw = (long)bits - (1L << width);
                    }

                    writer.WriteInteger(bitfield.Base.Kind, codec.EndianOf(bitfield.Base), raw);
                    break;
                }
                default:
                    throw Error($"Unsupported definition {definition.Kind}");
            }
        }

        private bool IsNullable(TypeRef type) => Project.Unalias(type) switch
        {
            OptionalRef => true,
            NativeRef native => Project.Bind(native).Type.Nullable,
            _ => false,
        };

        private JsonNode? FromExprValue(object? value, TypeRef type) => value switch
        {
            null => null,
            bool b => Project.Unalias(type) is PrimitiveRef { Kind: PrimitiveKind.Bool } ? JsonValue.Create(b) : JsonValue.Create(b ? 1 : 0),
            decimal d => Project.Unalias(type) is PrimitiveRef { Kind: PrimitiveKind.Bool } ? JsonValue.Create(d != 0) : JsonValues.FromInteger(d),
            double d => JsonValues.FromDouble(d),
            string s => JsonValue.Create(s),
            byte[] bytes => JsonValue.Create(Hex.Format(bytes)),
            _ => throw Error($"A computed value of type {value.GetType().Name} can't be written"),
        };
    }
}

public static class Bitfields
{
    /// <summary>Each member with the bit position of its least significant bit.</summary>
    public static IEnumerable<(BitFieldMember Member, int Shift)> Layout(BitfieldDef bitfield)
    {
        var width = Primitives.Bits(bitfield.Base.Kind);
        var used = 0;
        foreach (var member in bitfield.Members)
        {
            yield return (member, bitfield.LsbFirst ? used : width - used - member.Bits);
            used += member.Bits;
        }
    }
}
