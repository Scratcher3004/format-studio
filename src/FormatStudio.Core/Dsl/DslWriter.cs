using FormatStudio.Model;

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
                    map.AddIf("id", structDef.Id);
                    map.AddIf("groups", structDef.Groups.Cast<object?>().ToList());
                }

                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.Add("fields", structDef.Fields.Select(f => (object?)Field(f)).ToList());
                break;
            case EnumDef enumDef:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.Add("type", enumDef.Type.ToString());
                map.AddIf("flags", enumDef.Flags);
                map.AddIf("allowUnknown", enumDef.AllowUnknown);
                var values = new YMap();
                foreach (var member in enumDef.Members)
                {
                    if (member.Title == null && member.Doc == null)
                    {
                        values.Add(member.Name, member.Value);
                    }
                    else
                    {
                        var memberMap = new YMap { { "value", member.Value } };
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
                if (variant.Tag != null) map.Add("tag", Type(variant.Tag));
                map.Add("cases", variant.Cases.Select(c =>
                {
                    var caseMap = new YMap { { "name", c.Name }, { "tag", c.TagMember ?? (object?)c.TagValue } };
                    if (c.Type != null) caseMap.Add("type", Type(c.Type));
                    caseMap.AddIf("title", c.Title);
                    caseMap.AddIf("doc", c.Doc);
                    return (object?)caseMap;
                }).ToList());
                break;
            case AliasDef alias:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
                map.Add("type", Type(alias.Target));
                break;
            case BitfieldDef bitfield:
                map.AddIf("title", definition.Title);
                map.AddIf("doc", definition.Doc);
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
        if (field.Const != null) map.Add("const", field.Const);
        map.AddIf("computed", field.Computed);
        if (field.Default != null) map.Add("default", field.Default);
        map.AddIf("min", field.Min);
        map.AddIf("max", field.Max);
        map.AddIf("minLength", field.MinLength);
        map.AddIf("maxLength", field.MaxLength);
        map.AddIf("minItems", field.MinItems);
        map.AddIf("maxItems", field.MaxItems);
        map.AddIf("pattern", field.Pattern);
        return map;
    }

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
            case NbtRef nbt:
                map.Add("nbt", nbt.Flavor is { } flavor ? TypeExpressions.FlavorName(flavor) : "default");
                break;
        }

        // The emitter falls back to block style when a value is not a scalar.
        return map;
    }

    /// <summary>The type on one line: the short form, or the mapping form in flow style. <see cref="DslReader.ReadType"/> reads it back.</summary>
    public static string FormatType(TypeRef type) => TypeExpressions.TryFormat(type) ?? Flow(Type(type));

    private static string Flow(object? value) => value switch
    {
        YMap map => "{" + string.Join(", ", map.Select(pair => $"{pair.Key}: {Flow(pair.Value)}")) + "}",
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
