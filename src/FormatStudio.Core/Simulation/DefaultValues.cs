using System.Text.Json.Nodes;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

/// <summary>
/// Builds a template value for a type: zeros (within field constraints), empty strings and collections, absent optionals,
/// the first enum member and the first variant case. Shows the shape of the JSON a type expects.
/// </summary>
public sealed class DefaultValues
{
    private readonly FormatProject _project;
    private readonly ValueContext _context;

    public DefaultValues(FormatProject project)
    {
        _project = project;
        _context = new ValueContext(project, evaluateComputed: true);
    }

    /// <summary>Below this nesting depth, recursive types stop with null.</summary>
    public int MaxDepth { get; init; } = 16;

    public JsonNode? Generate(TypeRef type) => Generate(type, null, null, 0);

    private JsonNode? Generate(TypeRef type, FieldDef? field, StructScope? scope, int depth)
    {
        if (depth >= MaxDepth) return null;
        switch (type)
        {
            case PrimitiveRef primitive:
                return Primitive(primitive.Kind, field);
            case StringRef str:
            {
                var count = str.Length.Kind == LengthKind.Fixed && str.Padding == StringPadding.None ? str.Length.Fixed : field?.MinLength ?? 0;
                return JsonValue.Create(new string(' ', count));
            }
            case BytesRef bytes:
                return JsonValue.Create(Hex.Format(new byte[bytes.Length.Kind == LengthKind.Fixed ? bytes.Length.Fixed : 0]));
            case ArrayRef array:
            {
                var items = new JsonArray();
                var count = array.Length.Kind == LengthKind.Fixed ? array.Length.Fixed : field?.MinItems ?? 0;
                for (var i = 0; i < count; i++) items.Add(Generate(array.Element, null, scope, depth + 1));
                return items;
            }
            case MapRef:
                return new JsonArray();
            case OptionalRef:
                return null;
            case SizedRef sized:
                return Generate(sized.Inner, field, scope, depth + 1);
            case FixedPointRef:
                return Float();
            case NativeRef native:
            {
                var bound = _project.Bind(native);
                return bound.Type.DefaultValue(bound.Args);
            }
            case NamedRef named:
                return Named(named, field, scope, depth);
            default:
                return null;
        }
    }

    /// <summary>A zero that keeps its decimal point, so the template shows the value is a float.</summary>
    private static JsonNode Float() => JsonNode.Parse("0.0")!;

    private static JsonNode Primitive(PrimitiveKind kind, FieldDef? field)
    {
        switch (kind)
        {
            case PrimitiveKind.Bool:
                return JsonValue.Create(false);
            case PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64:
                return Float();
            case PrimitiveKind.Uuid:
                return JsonValue.Create(Guid.Empty.ToString());
        }

        var (min, max) = Primitives.Range(kind);
        if (field?.Min is { } fieldMin && fieldMin > min) min = Math.Ceiling(fieldMin);
        if (field?.Max is { } fieldMax && fieldMax < max) max = Math.Floor(fieldMax);
        return JsonValues.FromInteger(min > max ? min : Math.Clamp(0, min, max));
    }

    private JsonNode? Named(NamedRef named, FieldDef? field, StructScope? scope, int depth)
    {
        switch (_project.Find(named.Name))
        {
            case AliasDef alias:
                return Generate(alias.Target, field, scope, depth);
            case StructDef structDef:
                return Struct(structDef, scope, depth);
            case EnumDef enumDef:
                return enumDef.Members.Count == 0 ? JsonValue.Create(0) : JsonValue.Create(enumDef.Members[0].Name);
            case VariantDef variant:
            {
                if (variant.Cases.Count == 0) return null;
                var variantCase = variant.Cases[0];
                if (named.Tag != null && scope != null)
                {
                    // External tag: pick the case selected by the earlier field.
                    try
                    {
                        variantCase = _context.FindCase(variant, _context.Evaluator.Evaluate(Expr.Parse(named.Tag), scope)) ?? variantCase;
                    }
                    catch (ExprException)
                    {
                    }
                }

                return new JsonObject { [variantCase.Name] = variantCase.Type == null ? null : Generate(variantCase.Type, null, scope, depth + 1) };
            }
            case BitfieldDef bitfield:
            {
                var obj = new JsonObject();
                foreach (var member in bitfield.Members)
                {
                    obj[member.Name] = member.Type switch
                    {
                        null => JsonValue.Create(0),
                        "bool" => JsonValue.Create(false),
                        var enumName => _project.Find(enumName) is EnumDef { Members.Count: > 0 } e
                            ? JsonValue.Create(e.Members[0].Name)
                            : JsonValue.Create(0),
                    };
                }

                return obj;
            }
            default:
                return null;
        }
    }

    private JsonObject Struct(StructDef structDef, StructScope? parent, int depth)
    {
        var obj = new JsonObject();
        var scope = new StructScope(_context, structDef, obj, parent);
        foreach (var field in structDef.Fields)
        {
            if (field.Const != null || field.Computed != null) continue;
            if (field.If != null)
            {
                try
                {
                    if (!_context.Evaluator.EvaluateBool(Expr.Parse(field.If), scope)) continue;
                }
                catch (ExprException)
                {
                    // Depends on something absent (e.g. a computed presence flag): leave the field out.
                    continue;
                }
            }

            obj[field.Name] = field.Default?.DeepClone() ?? Generate(field.Type, field, scope, depth + 1);
        }

        return obj;
    }
}
