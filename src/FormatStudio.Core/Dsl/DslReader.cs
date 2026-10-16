using System.Globalization;
using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Natives;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FormatStudio.Dsl;

/// <summary>Reads one definition (a packet, struct, enum, variant, alias or bitfield) from a DSL file.</summary>
public sealed class DslReader
{
    private static readonly string[] KindKeys = ["packet", "struct", "enum", "variant", "alias", "bitfield"];
    private static readonly HashSet<string> CommonKeys = ["title", "doc", "skipCodegen"];

    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;

    private DslReader(string file, DiagnosticBag diagnostics)
    {
        _file = file;
        _diagnostics = diagnostics;
    }

    /// <summary>Parses the file. Problems are added to <paramref name="diagnostics"/>; returns null if nothing usable was found.</summary>
    public static TypeDef? Read(string text, string file, DiagnosticBag diagnostics)
    {
        var reader = new DslReader(file, diagnostics);
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0)
            {
                diagnostics.Error("The file is empty", new SourceLocation(file));
                return null;
            }

            if (stream.Documents.Count > 1)
                diagnostics.Warning("Only the first YAML document is used", new SourceLocation(file));
            return reader.ReadDefinition(stream.Documents[0].RootNode);
        }
        catch (YamlException e)
        {
            diagnostics.Error(e.InnerException?.Message ?? e.Message, new SourceLocation(file, (int)e.Start.Line, (int)e.Start.Column));
        }
        catch (FormatDefinitionException e)
        {
            diagnostics.Error(e.Message, e.Location ?? new SourceLocation(file));
        }

        return null;
    }

    /// <summary>Parses a type expression in either form (string or mapping).</summary>
    public static TypeRef ReadType(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return new DslReader("<input>", []).ParseType(stream.Documents[0].RootNode);
    }

    private SourceLocation Where(YamlNode node) => new(_file, (int)node.Start.Line, (int)node.Start.Column);

    private FormatDefinitionException Fail(YamlNode node, string message) => new(message, Where(node));

    private YamlMappingNode Map(YamlNode node, string what) =>
        node as YamlMappingNode ?? throw Fail(node, $"Expected a mapping for {what}");

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private string Scalar(YamlNode node, string what) => node is YamlScalarNode { Value: { } value }
        ? value
        : throw Fail(node, $"Expected a scalar for {what}");

    private string? OptionalString(YamlMappingNode map, string key) =>
        Get(map, key) is { } node ? Scalar(node, key) : null;

    private string Identifier(YamlNode node, string what)
    {
        var text = Scalar(node, what);
        if (!TypeExpressions.IsIdentifier(text)) throw Fail(node, $"'{text}' is not a valid {what} ([A-Za-z_][A-Za-z0-9_]*)");
        return text;
    }

    private long Integer(YamlNode node, string what)
    {
        var text = Scalar(node, what);
        if (YamlValues.InterpretPlain(text) is JsonValue value && value.TryGetValue<long>(out var result)) return result;
        throw Fail(node, $"Expected an integer for {what}, got '{text}'");
    }

    /// <summary>The base a plain integer scalar is written in, decimal for anything else.</summary>
    private static NumberBase BaseOf(YamlNode? node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: { } text } && YamlValues.TryParseInteger(text, out _, out var numberBase)
            ? numberBase
            : NumberBase.Decimal;

    private decimal Number(YamlNode node, string what)
    {
        var text = Scalar(node, what);
        if (YamlValues.TryParseInteger(text, out var integer, out _)) return integer;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) return result;
        throw Fail(node, $"Expected a number for {what}, got '{text}'");
    }

    private bool Bool(YamlNode node, string what) => Scalar(node, what) switch
    {
        "true" or "True" or "TRUE" => true,
        "false" or "False" or "FALSE" => false,
        var text => throw Fail(node, $"Expected true or false for {what}, got '{text}'"),
    };

    private void CheckKeys(YamlMappingNode map, ICollection<string> allowed, string what)
    {
        foreach (var key in map.Children.Keys)
        {
            var name = (key as YamlScalarNode)?.Value ?? "";
            if (!allowed.Contains(name) && !CommonKeys.Contains(name))
                _diagnostics.Warning($"Unknown key '{name}' in {what}", Where(key));
        }
    }

    private TypeDef ReadDefinition(YamlNode root)
    {
        var map = Map(root, "the definition");
        var kinds = KindKeys.Where(kind => Get(map, kind) != null).ToList();
        if (kinds.Count != 1)
            throw Fail(root, "A definition needs exactly one of: " + string.Join(", ", KindKeys) + " (with the name as value)");

        var kind = kinds[0];
        var name = Identifier(Get(map, kind)!, $"{kind} name");
        TypeDef definition = kind switch
        {
            "packet" or "struct" => ReadStruct(map, name, kind == "packet"),
            "enum" => ReadEnum(map, name),
            "variant" => ReadVariant(map, name),
            "alias" => ReadAlias(map, name),
            _ => ReadBitfield(map, name),
        };
        definition.Title = OptionalString(map, "title");
        definition.Doc = OptionalString(map, "doc");
        definition.SkipCodegen = Get(map, "skipCodegen") is { } skip ? ReadSkip(skip) : null;
        definition.Source = Where(root);
        return definition;
    }

    /// <summary><c>skipCodegen: true</c> (every target) or a list of target and generator names.</summary>
    private CodegenSkip? ReadSkip(YamlNode node)
    {
        if (node is YamlSequenceNode sequence)
        {
            var names = sequence.Children.Select(n => Scalar(n, "skipCodegen")).Distinct(StringComparer.Ordinal).ToList();
            return names.Count > 0 ? new CodegenSkip(false, names) : null;
        }

        var text = Scalar(node, "skipCodegen");
        return text switch
        {
            "true" or "True" or "TRUE" => CodegenSkip.Everywhere,
            "false" or "False" or "FALSE" or "" => null,
            _ => new CodegenSkip(false, [text]),
        };
    }

    private StructDef ReadStruct(YamlMappingNode map, string name, bool packet)
    {
        CheckKeys(map, packet ? ["packet", "id", "groups", "fields"] : ["struct", "fields"], packet ? "a packet" : "a struct");
        var definition = new StructDef { Name = name, IsPacket = packet };
        if (packet)
        {
            if (Get(map, "id") is { } id)
            {
                definition.Id = Integer(id, "id");
                definition.IdBase = BaseOf(id);
            }

            if (Get(map, "groups") is { } groups)
            {
                if (groups is YamlSequenceNode sequence)
                    definition.Groups.AddRange(sequence.Children.Select(group => Scalar(group, "group")));
                else
                    definition.Groups.Add(Scalar(groups, "groups"));
            }
        }

        if (Get(map, "fields") is { } fields)
        {
            if (fields is not YamlSequenceNode sequence) throw Fail(fields, "'fields' must be a list");
            foreach (var field in sequence.Children) definition.Fields.Add(ReadField(field));
        }

        return definition;
    }

    private static readonly string[] FieldKeys =
    [
        "name", "type", "if", "const", "computed", "default", "min", "max", "minLength", "maxLength", "minItems", "maxItems",
        "pattern", "length", "as", "tag", "encoding",
    ];

    private FieldDef ReadField(YamlNode node)
    {
        var map = Map(node, "a field");
        if (Get(map, "name") == null)
        {
            // Shorthand: "- fieldName: type"
            if (map.Children.Count != 1) throw Fail(node, "A field needs a 'name' key or the form '- name: type'");
            var (key, value) = map.Children.First();
            return new FieldDef { Name = Identifier(key, "field name"), Type = ParseType(value), Source = Where(node) };
        }

        CheckKeys(map, FieldKeys, "a field");
        var typeNode = Get(map, "type");
        var constNode = Get(map, "const");
        if (typeNode == null)
        {
            throw Fail(node, map.Children.Count == 1 && Get(map, "name") != null
                ? "A field needs a 'type'. '- name: T' is the long form without a type; a field called 'name' needs '- {name: name, type: T}'"
                : "A field needs a 'type'");
        }

        var field = new FieldDef
        {
            Name = Identifier(Get(map, "name")!, "field name"),
            Type = ApplyModifiers(ParseType(typeNode), map),
            Title = OptionalString(map, "title"),
            Doc = OptionalString(map, "doc"),
            If = OptionalString(map, "if"),
            Computed = OptionalString(map, "computed"),
            Const = constNode != null ? YamlValues.ToJson(constNode) : null,
            ConstBase = BaseOf(constNode),
            Default = Get(map, "default") is { } defaultNode ? YamlValues.ToJson(defaultNode) : null,
            DefaultBase = BaseOf(Get(map, "default")),
            Min = Get(map, "min") is { } min ? Number(min, "min") : null,
            MinBase = BaseOf(Get(map, "min")),
            Max = Get(map, "max") is { } max ? Number(max, "max") : null,
            MaxBase = BaseOf(Get(map, "max")),
            MinLength = Get(map, "minLength") is { } minLength ? (int)Integer(minLength, "minLength") : null,
            MaxLength = Get(map, "maxLength") is { } maxLength ? (int)Integer(maxLength, "maxLength") : null,
            MinItems = Get(map, "minItems") is { } minItems ? (int)Integer(minItems, "minItems") : null,
            MaxItems = Get(map, "maxItems") is { } maxItems ? (int)Integer(maxItems, "maxItems") : null,
            Pattern = OptionalString(map, "pattern"),
            Source = Where(node),
        };
        if (constNode != null && field.Const == null) throw Fail(constNode, "'const' must not be null");
        if (field.Const != null && field.Computed != null) throw Fail(node, "A field can't be both 'const' and 'computed'");
        return field;
    }

    /// <summary>Applies the field-level shortcuts <c>length</c>, <c>as</c>, <c>tag</c> and <c>encoding</c> to the type.</summary>
    private TypeRef ApplyModifiers(TypeRef type, YamlMappingNode map)
    {
        var length = Get(map, "length");
        var asNode = Get(map, "as");
        var tag = Get(map, "tag");
        var encoding = Get(map, "encoding");
        if (length == null && asNode == null && tag == null && encoding == null) return type;
        return Apply(type);

        TypeRef Apply(TypeRef current)
        {
            switch (current)
            {
                case OptionalRef optional:
                    return optional with { Inner = Apply(optional.Inner) };
                case ArrayRef array when length != null:
                    return array with { Length = ParseLength(length) };
                case MapRef mapRef when length != null:
                    return mapRef with { Length = ParseLength(length) };
                case SizedRef sized when length != null:
                    return sized with { Length = ParseLength(length) };
                case StringRef str when length != null || encoding != null:
                    return new StringRef(encoding != null ? ParseEncoding(encoding) : str.Encoding,
                        length != null ? ParseLength(length) : str.Length);
                case BytesRef when length != null:
                    return new BytesRef(ParseLength(length));
                case NamedRef named when asNode != null || tag != null:
                    return named with
                    {
                        As = asNode != null ? ParsePrimitive(asNode) : named.As,
                        Tag = tag != null ? Scalar(tag, "tag") : named.Tag,
                    };
                default:
                    throw Fail(map, $"'length', 'as', 'tag' or 'encoding' don't apply to type {current}");
            }
        }
    }

    private StringEncoding ParseEncoding(YamlNode node)
    {
        try
        {
            return TypeExpressions.ParseEncoding(Scalar(node, "encoding"));
        }
        catch (System.FormatException e)
        {
            throw Fail(node, e.Message);
        }
    }

    private PrimitiveRef ParsePrimitive(YamlNode node)
    {
        var text = Scalar(node, "a primitive type");
        return Primitives.TryParse(text, out var primitive) ? primitive : throw Fail(node, $"'{text}' is not a primitive type");
    }

    private LengthSpec ParseLength(YamlNode node)
    {
        try
        {
            return TypeExpressions.ParseLength(Scalar(node, "length"));
        }
        catch (System.FormatException e)
        {
            throw Fail(node, e.Message);
        }
    }

    private static readonly string[] TypeMapKeys =
        ["array", "string", "bytes", "map", "optional", "sized", "fixed", "native", "type", .. NativeTypes.BuiltIn.All.Select(t => t.Name)];

    /// <summary>
    /// One argument, or a list of them; <c>default</c> alone means none. A type argument may be a type mapping; it is
    /// kept as text, in the flow style.
    /// </summary>
    private List<string> NativeArgs(YamlNode node) => node is YamlSequenceNode sequence
        ? sequence.Children.Select(NativeArg).ToList()
        : node is YamlMappingNode ? [NativeArg(node)] : Scalar(node, "an argument") is var arg && arg is "" or "default" ? [] : [arg];

    private string NativeArg(YamlNode node) => node is YamlMappingNode ? DslWriter.FormatType(ParseType(node)) : Scalar(node, "an argument");

    internal TypeRef ParseType(YamlNode node)
    {
        if (node is YamlScalarNode)
        {
            try
            {
                return TypeExpressions.Parse(Scalar(node, "type"));
            }
            catch (System.FormatException e)
            {
                throw Fail(node, e.Message);
            }
        }

        var map = Map(node, "a type");
        var keys = TypeMapKeys.Where(key => Get(map, key) != null).ToList();
        if (keys.Count != 1) throw Fail(node, "A type mapping needs exactly one of: " + string.Join(", ", TypeMapKeys));
        var kind = keys[0];
        var value = Get(map, kind)!;
        var length = Get(map, "length") is { } lengthNode ? ParseLength(lengthNode) : LengthSpec.Default;

        switch (kind)
        {
            case "array":
                CheckKeys(map, ["array", "length", "until"], "an array type");
                if (Get(map, "until") is { } until)
                {
                    if (Get(map, "length") != null) throw Fail(node, "An array has either 'length' or 'until'");
                    return new ArrayRef(ParseType(value), new LengthSpec(LengthKind.Until, Expression: Scalar(until, "until")));
                }

                return new ArrayRef(ParseType(value), length);
            case "string":
                CheckKeys(map, ["string", "length"], "a string type");
                return new StringRef(Scalar(value, "string") is "" or "default" ? null : ParseEncoding(value), length);
            case "bytes":
                CheckKeys(map, ["bytes"], "a bytes type");
                return new BytesRef(ParseLength(value));
            case "map":
                CheckKeys(map, ["map", "length"], "a map type");
                var entry = Map(value, "map key and value");
                var key = Get(entry, "key") ?? throw Fail(value, "A map needs a 'key' type");
                var item = Get(entry, "value") ?? throw Fail(value, "A map needs a 'value' type");
                return new MapRef(ParseType(key), ParseType(item), length);
            case "optional":
                CheckKeys(map, ["optional"], "an optional type");
                return new OptionalRef(ParseType(value));
            case "sized":
                CheckKeys(map, ["sized", "length"], "a sized type");
                return new SizedRef(ParseType(value), length);
            case "fixed":
                CheckKeys(map, ["fixed", "fraction"], "a fixed-point type");
                var fraction = Get(map, "fraction") ?? throw Fail(node, "A fixed-point type needs 'fraction' (number of fraction bits)");
                var basePrimitive = ParsePrimitive(value);
                if (!Primitives.IsInteger(basePrimitive.Kind)) throw Fail(value, "A fixed-point base must be an integer type");
                return new FixedPointRef(basePrimitive, (int)Integer(fraction, "fraction"));
            case "native":
                CheckKeys(map, ["native", "args"], "a native type");
                var name = Scalar(value, "native");
                if (!TypeExpressions.IsTypeName(name)) throw Fail(value, $"'{name}' is not a native type name");
                return new NativeRef(name, Get(map, "args") is { } args ? NativeArgs(args) : []);
            case var builtIn when NativeTypes.IsBuiltIn(builtIn):
                CheckKeys(map, [builtIn], "a native type");
                return new NativeRef(builtIn, NativeArgs(value));
            default:
                CheckKeys(map, ["type", "as", "tag"], "a type reference");
                var inner = ParseType(value);
                if (Get(map, "as") == null && Get(map, "tag") == null) return inner;
                if (inner is not NamedRef named) throw Fail(node, "'as' and 'tag' only apply to named types");
                return named with
                {
                    As = Get(map, "as") is { } asNode ? ParsePrimitive(asNode) : null,
                    Tag = OptionalString(map, "tag"),
                };
        }
    }

    private EnumDef ReadEnum(YamlMappingNode map, string name)
    {
        CheckKeys(map, ["enum", "type", "flags", "allowUnknown", "values"], "an enum");
        var definition = new EnumDef
        {
            Name = name,
            Type = Get(map, "type") is { } type ? ParsePrimitive(type) : new PrimitiveRef(PrimitiveKind.VarUInt32),
            Flags = Get(map, "flags") is { } flags && Bool(flags, "flags"),
            AllowUnknown = Get(map, "allowUnknown") is { } allowUnknown && Bool(allowUnknown, "allowUnknown"),
        };
        if (!Primitives.IsInteger(definition.Type.Kind)) throw Fail(Get(map, "type")!, "An enum type must be an integer type");

        if (Get(map, "values") is { } values)
        {
            long next = 0;
            foreach (var (key, value) in Map(values, "enum values").Children)
            {
                var member = new EnumMember { Name = Identifier(key, "enum member name") };
                if (value is YamlMappingNode memberMap)
                {
                    CheckKeys(memberMap, ["value"], "an enum member");
                    member.Value = Get(memberMap, "value") is { } v ? Integer(v, "value") : next;
                    member.Base = BaseOf(Get(memberMap, "value"));
                    member.Title = OptionalString(memberMap, "title");
                    member.Doc = OptionalString(memberMap, "doc");
                }
                else
                {
                    member.Value = value is YamlScalarNode { Value: "" or null, Style: ScalarStyle.Plain } ? next : Integer(value, "value");
                    member.Base = BaseOf(value);
                }

                if (definition.FindByName(member.Name) != null) throw Fail(key, $"Duplicate enum member '{member.Name}'");
                definition.Members.Add(member);
                next = member.Value + 1;
            }
        }

        return definition;
    }

    private VariantDef ReadVariant(YamlMappingNode map, string name)
    {
        CheckKeys(map, ["variant", "tag", "cases"], "a variant");
        var definition = new VariantDef { Name = name, Tag = Get(map, "tag") is { } tag ? ParseType(tag) : null };
        if (definition.Tag is not (null or PrimitiveRef or NamedRef or StringRef))
            throw Fail(Get(map, "tag")!, "A variant tag must be a primitive, a string or an enum");

        if (Get(map, "cases") is { } cases)
        {
            if (cases is not YamlSequenceNode sequence) throw Fail(cases, "'cases' must be a list");
            long next = 0;
            foreach (var caseNode in sequence.Children)
            {
                var caseMap = Map(caseNode, "a variant case");
                CheckKeys(caseMap, ["name", "tag", "type"], "a variant case");
                var variantCase = new VariantCase
                {
                    Name = Identifier(Get(caseMap, "name") ?? throw Fail(caseNode, "A case needs a 'name'"), "case name"),
                    Type = Get(caseMap, "type") is { } caseType ? ParseType(caseType) : null,
                    Title = OptionalString(caseMap, "title"),
                    Doc = OptionalString(caseMap, "doc"),
                };
                var tagNode = Get(caseMap, "tag");
                if (tagNode == null)
                {
                    variantCase.TagValue = next;
                }
                else if (YamlValues.InterpretPlain(Scalar(tagNode, "tag")) is JsonValue value && value.TryGetValue<long>(out var number))
                {
                    variantCase.TagValue = number;
                    variantCase.TagBase = BaseOf(tagNode);
                }
                else
                {
                    // An enum member name or a string tag; the validator checks it against the tag type.
                    variantCase.TagMember = Scalar(tagNode, "tag");
                }

                if (definition.Cases.Any(c => c.Name == variantCase.Name)) throw Fail(caseNode, $"Duplicate case '{variantCase.Name}'");
                definition.Cases.Add(variantCase);
                next = (variantCase.TagValue ?? next) + 1;
            }
        }

        return definition;
    }

    private AliasDef ReadAlias(YamlMappingNode map, string name)
    {
        CheckKeys(map, ["alias", "type"], "an alias");
        var type = Get(map, "type") ?? throw Fail(map, "An alias needs a 'type'");
        return new AliasDef { Name = name, Target = ParseType(type) };
    }

    private BitfieldDef ReadBitfield(YamlMappingNode map, string name)
    {
        CheckKeys(map, ["bitfield", "type", "lsbFirst", "fields"], "a bitfield");
        var definition = new BitfieldDef
        {
            Name = name,
            Base = Get(map, "type") is { } type ? ParsePrimitive(type) : throw Fail(map, "A bitfield needs a 'type'"),
            LsbFirst = Get(map, "lsbFirst") is { } lsb && Bool(lsb, "lsbFirst"),
        };
        if (!Primitives.IsInteger(definition.Base.Kind))
            throw Fail(map, "A bitfield type must be an integer type");

        if (Get(map, "fields") is not YamlSequenceNode fields) throw Fail(map, "A bitfield needs a 'fields' list");
        foreach (var fieldNode in fields.Children)
        {
            var fieldMap = Map(fieldNode, "a bitfield member");
            BitFieldMember member;
            if (Get(fieldMap, "name") == null && fieldMap.Children.Count == 1)
            {
                var (key, value) = fieldMap.Children.First();
                member = new BitFieldMember { Name = Identifier(key, "member name"), Bits = (int)Integer(value, "bits") };
            }
            else
            {
                CheckKeys(fieldMap, ["name", "bits", "signed", "type"], "a bitfield member");
                member = new BitFieldMember
                {
                    Name = Identifier(Get(fieldMap, "name") ?? throw Fail(fieldNode, "A member needs a 'name'"), "member name"),
                    Bits = (int)Integer(Get(fieldMap, "bits") ?? throw Fail(fieldNode, "A member needs 'bits'"), "bits"),
                    Signed = Get(fieldMap, "signed") is { } signed && Bool(signed, "signed"),
                    Type = OptionalString(fieldMap, "type"),
                    Doc = OptionalString(fieldMap, "doc"),
                };
            }

            if (member.Bits <= 0) throw Fail(fieldNode, "'bits' must be positive");
            definition.Members.Add(member);
        }

        var total = definition.Members.Sum(m => m.Bits);
        if (total > Primitives.Bits(definition.Base.Kind))
            throw Fail(map, $"The members use {total} bits but {definition.Base} only has {Primitives.Bits(definition.Base.Kind)}");
        return definition;
    }
}
