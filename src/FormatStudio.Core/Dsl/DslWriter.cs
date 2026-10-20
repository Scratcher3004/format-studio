using System.Text.Json;
using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Natives;

namespace FormatStudio.Dsl;

/// <summary>Writes a definition in the DSL. <see cref="DslReader"/> reads the output back to an equal definition.</summary>
public static class DslWriter
{
    public static string Write(TypeDef definition) => YamlEmitter.Write(ToYaml(definition));

    public static YMap ToYaml(TypeDef definition)
    {
        var map = new YMap { { definition.Kind, definition.Name } };
        switch (definition)
        {
            case StructDef structDef:
                if (structDef.IsPacket)
                {
                    map.AddIf("id", Integer(structDef.Id, structDef.IdBase));
                    map.AddIf("groups", structDef.Groups.Cast<object?>().ToList());
                }

                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.AddIf("skipCodegen", Skip(definition.SkipCodegen));
                map.Add("fields", structDef.Fields.Select(f => (object?)Field(f)).ToList());
                break;
            case EnumDef enumDef:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.AddIf("skipCodegen", Skip(definition.SkipCodegen));
                map.Add("type", enumDef.Type.ToString());
                map.AddIf("flags", enumDef.Flags);
                map.AddIf("allowUnknown", enumDef.AllowUnknown);
                var values = new YMap();
                foreach (var member in enumDef.Members)
                {
                    if (member.Title == null && member.Doc == null)
                    {
                        values.Add(member.Name, Integer(member.Value, member.Base));
                    }
                    else
                    {
                        var memberMap = new YMap { { "value", Integer(member.Value, member.Base) } };
                        memberMap.AddIf("title", member.Title);
                        memberMap.AddIf("doc", member.Doc);
                        values.Add(member.Name, memberMap);
                    }
                }

                map.Add("values", values);
                break;
            case VariantDef variant:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.AddIf("skipCodegen", Skip(definition.SkipCodegen));
                if (variant.Tag != null) map.Add("tag", Type(variant.Tag));
                map.Add("cases", variant.Cases.Select(c =>
                {
                    var caseMap = new YMap { { "name", c.Name }, { "tag", (object?)c.TagMember ?? Integer(c.TagValue, c.TagBase) } };
                    if (c.Type != null) caseMap.Add("type", Type(c.Type));
                    caseMap.AddIf("title", c.Title);
                    caseMap.AddIf("doc", c.Doc);
                    return (object?)caseMap;
                }).ToList());
                break;
            case AliasDef alias:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.AddIf("skipCodegen", Skip(definition.SkipCodegen));
                map.Add("type", Type(alias.Target));
                break;
            case BitfieldDef bitfield:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.AddIf("skipCodegen", Skip(definition.SkipCodegen));
                map.Add("type", bitfield.Base.ToString());
                map.AddIf("lsbFirst", bitfield.LsbFirst);
                map.Add("fields", bitfield.Members.Select(m =>
                {
                    if (!m.Signed && m.Type == null && m.Doc == null) return (object?)new YMap { { m.Name, m.Bits } };
                    var memberMap = new YMap { { "name", m.Name }, { "bits", m.Bits } };
                    memberMap.AddIf("signed", m.Signed);
                    memberMap.AddIf("type", m.Type);
                    memberMap.AddIf("doc", m.Doc);
                    return memberMap;
                }).ToList());
                break;
        }

        return map;
    }

    private static object? Skip(CodegenSkip? skip) => skip switch
    {
        null => null,
        { All: true } => true,
        _ => skip.Targets.Cast<object?>().ToList(),
    };

    /// <summary>
    /// Sets <c>skipCodegen</c> in a definition's YAML and keeps the rest of the text, comments included: replaces the
    /// entry, or adds it after <c>doc</c>, <c>title</c> or the packet header like <see cref="Write"/> does.
    /// </summary>
    public static string SetSkipCodegen(string text, CodegenSkip? skip)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var entry = skip is null or { All: false, Targets.Count: 0 } ? [] : YamlEmitter.Write(new YMap { { "skipCodegen", Skip(skip) } }).TrimEnd('\n').Split('\n');

        var keys = Enumerable.Range(0, lines.Count).Select(i => (Index: i, Key: TopLevelKey(lines[i]))).Where(k => k.Key != null).ToList();
        if (keys.FirstOrDefault(k => k.Key == "skipCodegen") is { Key: not null } existing)
        {
            lines.RemoveRange(existing.Index, EntryEnd(lines, existing.Index) - existing.Index);
            lines.InsertRange(existing.Index, entry);
        }
        else if (entry.Length > 0)
        {
            var after = keys.Where((k, i) => i == 0 || k.Key is "id" or "groups" or "title" or "doc").Select(k => k.Index).DefaultIfEmpty(-1).Max();
            lines.InsertRange(after < 0 ? 0 : EntryEnd(lines, after), entry);
        }

        return string.Join(newline, lines);
    }

    private static string? TopLevelKey(string line)
    {
        var colon = line.IndexOf(':');
        return colon > 0 && line[0] is not (' ' or '\t' or '-' or '#') && line[..colon].All(c => char.IsLetterOrDigit(c) || c == '_') ? line[..colon] : null;
    }

    /// <summary>The line after a top-level entry: its value continues on indented lines and <c>- </c> items.</summary>
    private static int EntryEnd(List<string> lines, int start)
    {
        var end = start + 1;
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (lines[i][0] is not (' ' or '\t' or '-')) break;
            end = i + 1;
        }

        return end;
    }

    private static object Field(FieldDef field)
    {
        var simple = field.Title == null && field.Doc == null && field.If == null && field.Const == null && field.Computed == null
            && field.Default == null && field.Min == null && field.Max == null && field.MinLength == null && field.MaxLength == null
            && field.MinItems == null && field.MaxItems == null && field.Pattern == null;
        if (simple && TypeExpressions.TryFormat(field.Type) is { } shortType) return new YMap { { field.Name, shortType } };

        var map = new YMap { { "name", field.Name }, { "type", Type(field.Type) } };
        map.AddIf("title", field.Title);
        map.AddIf("doc", field.Doc);
        map.AddIf("if", field.If);
        if (field.Const != null) map.Add("const", Value(field.Const, field.ConstBase));
        map.AddIf("computed", field.Computed);
        if (field.Default != null) map.Add("default", Value(field.Default, field.DefaultBase));
        map.AddIf("min", Number(field.Min, field.MinBase));
        map.AddIf("max", Number(field.Max, field.MaxBase));
        map.AddIf("minLength", field.MinLength);
        map.AddIf("maxLength", field.MaxLength);
        map.AddIf("minItems", field.MinItems);
        map.AddIf("maxItems", field.MaxItems);
        map.AddIf("pattern", field.Pattern);
        return map;
    }

    private static YamlInteger? Integer(long? value, NumberBase numberBase) => value is { } v ? new YamlInteger(v, numberBase) : null;

    /// <summary>Integral numbers in their base, others as they are.</summary>
    private static object? Number(decimal? value, NumberBase numberBase) =>
        numberBase != NumberBase.Decimal && value is { } v && v == decimal.Truncate(v) && v is >= long.MinValue and <= long.MaxValue ? new YamlInteger((long)v, numberBase) : value;

    /// <summary>An integer value in its base, any other value as it is.</summary>
    private static object Value(JsonNode value, NumberBase numberBase) =>
        numberBase != NumberBase.Decimal && value is JsonValue json && json.GetValueKind() == JsonValueKind.Number && json.TryGetValue<long>(out var integer)
            ? new YamlInteger(integer, numberBase)
            : value;

    /// <summary>The short string form when possible, otherwise the mapping form.</summary>
    public static object Type(TypeRef type)
    {
        if (TypeExpressions.TryFormat(type) is { } text) return text;

        var map = new YMap { Flow = true };
        switch (type)
        {
            case NamedRef named:
                map.Add("type", named.Name);
                if (named.As != null) map.Add("as", named.As.ToString());
                if (named.Tag != null) map.Add("tag", named.Tag);
                break;
            case StringRef str:
                map.Add("string", str.Encoding is { } encoding ? TypeExpressions.EncodingName(encoding) : "default");
                AddLength(map, str.Length);
                if (str.Padding != StringPadding.None) map.Add("pad", TypeExpressions.FormatPad(str.Padding, str.Pad));
                break;
            case BytesRef bytes:
                map.Add("bytes", bytes.Length.Kind == LengthKind.Default ? "default" : LengthValue(bytes.Length));
                break;
            case ArrayRef { Length.Kind: LengthKind.Until } array:
                map.Add("array", Type(array.Element));
                map.Add("until", array.Length.Expression!);
                break;
            case ArrayRef array:
                map.Add("array", Type(array.Element));
                AddLength(map, array.Length);
                break;
            case MapRef mapRef:
                map.Add("map", new YMap { { "key", Type(mapRef.Key) }, { "value", Type(mapRef.Value) } });
                AddLength(map, mapRef.Length);
                break;
            case OptionalRef optional:
                map.Add("optional", Type(optional.Inner));
                break;
            case SizedRef sized:
                map.Add("sized", Type(sized.Inner));
                AddLength(map, sized.Length);
                break;
            case FixedPointRef fixedPoint:
                map.Add("fixed", fixedPoint.Base.ToString());
                map.Add("fraction", fixedPoint.FractionBits);
                break;
            case NativeRef native when NativeTypes.IsBuiltIn(native.Name):
                map.Add(native.Name, native.Args.Count == 1 ? Argument(native.Args[0]) : native.Args.Select(Argument).ToList());
                break;
            case NativeRef native:
                map.Add("native", native.Name);
                if (native.Args.Count > 0) map.Add("args", native.Args.Select(Argument).ToList());
                break;
        }

        // The emitter falls back to block style when a value is not a scalar.
        return map;
    }

    /// <summary>A native type argument: a type argument in the mapping form (<c>{array: Chunk, length: u8}</c>) as a mapping, others as text.</summary>
    private static object? Argument(string arg)
    {
        if (!arg.StartsWith('{')) return arg;
        try
        {
            return Type(DslReader.ReadType(arg));
        }
        catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException)
        {
            return arg;
        }
    }

    /// <summary>The type on one line: the short form, or the mapping form in flow style. <see cref="DslReader.ReadType"/> reads it back.</summary>
    public static string FormatType(TypeRef type) => TypeExpressions.TryFormat(type) ?? Flow(Type(type));

    private static string Flow(object? value) => value switch
    {
        YMap map => "{" + string.Join(", ", map.Select(pair => $"{pair.Key}: {Flow(pair.Value)}")) + "}",
        List<object?> list => "[" + string.Join(", ", list.Select(Flow)) + "]",
        string text when text.Length == 0 || text.IndexOfAny([',', '[', ']', '{', '}', ':', '#', '"', '\'']) >= 0
            || YamlValues.NeedsQuotes(text) || text != text.Trim() => System.Text.Json.JsonSerializer.Serialize(text),
        string text => text,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "null",
    };

    private static object LengthValue(LengthSpec length) => length.Kind == LengthKind.Fixed ? length.Fixed : length.ToString();

    private static void AddLength(YMap map, LengthSpec length)
    {
        if (length.Kind != LengthKind.Default) map.Add("length", LengthValue(length));
    }
}
