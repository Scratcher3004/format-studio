using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

/// <summary>A decoded region of the input: one value with its byte range.</summary>
public sealed class SpanNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>The type as written, e.g. <c>varuint32</c> or <c>ActorLink[]</c>.</summary>
    public required string Type { get; init; }
    public int Offset { get; set; }
    public int Length { get; set; }
    /// <summary>A short display of the value for leaves.</summary>
    public string? Display { get; set; }
    public string? Error { get; set; }
    public List<SpanNode> Children { get; } = [];

    public IEnumerable<SpanNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));

    /// <summary>The deepest node containing the byte at <paramref name="offset"/>.</summary>
    public SpanNode? At(int offset)
    {
        if (offset < Offset || offset >= Offset + Length) return null;
        foreach (var child in Children)
            if (child.At(offset) is { } hit) return hit;
        return this;
    }
}

/// <summary>Helpers for the JSON representation of values.</summary>
public static class JsonValues
{
    public static decimal? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.Number) return double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
        if (value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                var text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            };
        }

        return null;
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static JsonNode FromDouble(double value) => double.IsFinite(value)
        ? JsonValue.Create(value)
        : JsonValue.Create(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");

    public static JsonNode FromInteger(decimal value) => value >= long.MinValue && value <= long.MaxValue
        ? JsonValue.Create((long)value)
        : JsonValue.Create((ulong)value);

    public static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        if (Number(a) is { } x && Number(b) is { } y && a!.GetValueKind() != JsonValueKind.String && b!.GetValueKind() != JsonValueKind.String)
            return x == y;
        return JsonNode.DeepEquals(a, b);
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToText(JsonNode? node, bool indented = true) => node?.ToJsonString(indented ? Indented : null) ?? "null";
}

/// <summary>Fields of a struct value for expression evaluation.</summary>
internal sealed class StructScope : IExprScope
{
    private readonly ValueContext _context;
    private readonly HashSet<string> _computing = [];

    public StructScope(ValueContext context, StructDef definition, JsonObject value, StructScope? parent)
    {
        _context = context;
        Definition = definition;
        Value = value;
        Parent = parent;
    }

    public StructDef Definition { get; }
    public JsonObject Value { get; }
    public StructScope? Parent { get; }
    public StructScope Root => Parent?.Root ?? this;

    public bool TryGet(string name, out object? value)
    {
        value = null;
        switch (name)
        {
            case "_parent":
                value = Parent;
                return Parent != null;
            case "_root":
                value = Root;
                return true;
        }

        var field = Definition.Fields.FirstOrDefault(f => f.Name == name);
        if (field == null) return false;

        if (field.Computed != null && _context.EvaluateComputed)
        {
            if (!_computing.Add(name)) throw new ExprException($"'{name}' depends on itself");
            try
            {
                value = _context.Evaluator.Evaluate(Expr.Parse(field.Computed), this);
                return true;
            }
            finally
            {
                _computing.Remove(name);
            }
        }

        var node = Value[name] ?? field.Const;
        value = _context.ToExprValue(node, field.Type, this);
        return true;
    }
}

/// <summary>Members of a bitfield, or any other name-to-value mapping, for expression evaluation.</summary>
internal sealed class DictionaryScope(Dictionary<string, object?> values) : IExprScope
{
    public bool TryGet(string name, out object? value) => values.TryGetValue(name, out value);
}

/// <summary>Converts JSON values into expression values.</summary>
internal sealed class ValueContext
{
    public ValueContext(FormatProject project, bool evaluateComputed)
    {
        Project = project;
        EvaluateComputed = evaluateComputed;
        Evaluator = new ExprEvaluator((enumName, member) =>
            Project.Find(enumName) is EnumDef e && e.FindByName(member) is { } m ? m.Value : null);
    }

    public FormatProject Project { get; }
    /// <summary>When encoding, computed fields are calculated from the other fields instead of read from the value.</summary>
    public bool EvaluateComputed { get; }
    public ExprEvaluator Evaluator { get; }

    public object? ToExprValue(JsonNode? node, TypeRef type, StructScope? parent)
    {
        if (node == null) return null;
        type = Project.Unalias(type);
        switch (type)
        {
            case PrimitiveRef { Kind: PrimitiveKind.Bool }:
                return node.GetValueKind() == JsonValueKind.True || JsonValues.Number(node) is { } n && n != 0;
            case PrimitiveRef { Kind: PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 }:
            case FixedPointRef:
                return JsonValues.Double(node);
            case PrimitiveRef { Kind: PrimitiveKind.Uuid }:
                return JsonValues.String(node);
            case PrimitiveRef:
                return JsonValues.Number(node);
            case StringRef:
                return JsonValues.String(node);
            case BytesRef:
                return JsonValues.String(node) is { } hex ? Hex.Parse(hex) : null;
            case NativeRef native:
            {
                var bound = Project.Bind(native);
                return bound.Type.ExpressionValue(node, bound.Args);
            }
            case OptionalRef optional:
                return ToExprValue(node, optional.Inner, parent);
            case SizedRef sized:
                return ToExprValue(node, sized.Inner, parent);
            case ArrayRef array:
                return node is JsonArray items ? items.Select(item => ToExprValue(item, array.Element, parent)).ToList() : null;
            case MapRef:
                return node is JsonObject mapObject ? mapObject.Select(pair => (object?)pair.Key).ToList() : (node as JsonArray)?.Cast<object?>().ToList();
            case NamedRef named:
                switch (Project.Find(named.Name))
                {
                    case EnumDef enumDef:
                        return EnumValue(enumDef, node);
                    case StructDef structDef when node is JsonObject obj:
                        return new StructScope(this, structDef, obj, parent);
                    case VariantDef variant when node is JsonObject obj && obj.Count == 1:
                        var (caseName, caseValue) = obj.First();
                        var variantCase = variant.Cases.FirstOrDefault(c => c.Name == caseName);
                        if (variantCase == null) return null;
                        return new VariantValue(ExprTag(variant, variantCase), caseName,
                            variantCase.Type != null ? ToExprValue(caseValue, variantCase.Type, parent) : null);
                    case VariantDef variant when JsonValues.String(node) is { } caseOnly:
                        var bare = variant.Cases.FirstOrDefault(c => c.Name == caseOnly);
                        return bare == null ? null : new VariantValue(ExprTag(variant, bare), caseOnly, null);
                    case BitfieldDef bitfield when node is JsonObject obj:
                        return new DictionaryScope(bitfield.Members.ToDictionary(m => m.Name, m => m.Type switch
                        {
                            null => (object?)JsonValues.Number(obj[m.Name]),
                            "bool" => ToExprValue(obj[m.Name], new PrimitiveRef(PrimitiveKind.Bool), parent),
                            var enumName => ToExprValue(obj[m.Name], new NamedRef(enumName), parent),
                        }));
                }

                return null;
            default:
                return null;
        }
    }

    public decimal? EnumValue(EnumDef enumDef, JsonNode? node)
    {
        if (JsonValues.String(node) is { } text)
        {
            if (enumDef.FindByName(text) is { } member) return member.Value;
            if (enumDef.Flags && text.Contains('|'))
            {
                long flags = 0;
                foreach (var part in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (enumDef.FindByName(part) is { } flag) flags |= flag.Value;
                    else if (JsonValues.Number(JsonValue.Create(part)) is { } number) flags |= (long)number;
                    else return null;
                }

                return flags;
            }
        }

        return JsonValues.Number(node);
    }

    /// <summary>The tag of a case: a long, or a string for string tags. Null when it can't be resolved.</summary>
    public object? CaseTag(VariantDef variant, VariantCase variantCase)
    {
        switch (Project.TagKind(variant))
        {
            case VariantTagKind.String:
                return variantCase.TagMember;
            case VariantTagKind.Enum when variantCase.TagMember != null:
                return Project.Find(((NamedRef)variant.Tag!).Name) is EnumDef enumDef && enumDef.FindByName(variantCase.TagMember) is { } member
                    ? member.Value
                    : null;
            default:
                return variantCase.TagValue;
        }
    }

    /// <summary>The tag as an expression value: a decimal or a string.</summary>
    private object ExprTag(VariantDef variant, VariantCase variantCase) => CaseTag(variant, variantCase) switch
    {
        long number => (decimal)number,
        { } text => text,
        null => 0m,
    };

    /// <summary>The case with the given tag (a number or a string, as read or evaluated).</summary>
    public VariantCase? FindCase(VariantDef variant, object? tag)
    {
        var key = NormalizeTag(tag);
        return key == null ? null : variant.Cases.FirstOrDefault(c => Equals(CaseTag(variant, c), key));
    }

    /// <summary>Tags as longs or strings, so that a decimal from an expression equals the long of a case.</summary>
    public static object? NormalizeTag(object? tag) => tag switch
    {
        decimal d when d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue => (long)d,
        long or string => tag,
        int i => (long)i,
        _ => null,
    };
}
