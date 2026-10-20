using System.Text;
using System.Text.Json.Nodes;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

/// <summary>
/// Generates random values that fit a type: numbers within the wire range and field constraints, short strings and
/// collections, conditions and external tags that agree with earlier fields.
/// </summary>
public sealed class RandomValues
{
    private readonly FormatProject _project;
    private readonly Random _random;
    private readonly ValueContext _context;

    public RandomValues(FormatProject project, int seed) : this(project, new Random(seed))
    {
    }

    private RandomValues(FormatProject project, Random random)
    {
        _project = project;
        _random = random;
        _context = new ValueContext(project, evaluateComputed: true);
    }

    /// <summary>Maximum number of elements in arrays, maps, strings and bytes.</summary>
    public int MaxItems { get; init; } = 4;
    /// <summary>Below this nesting depth, optional values are absent and collections empty.</summary>
    public int MaxDepth { get; init; } = 6;

    public JsonNode? Generate(TypeRef type) => Generate(type, null, null, 0);

    /// <summary>A value inside the value of a native type (see <see cref="Natives.NativeArguments.RandomValue"/>).</summary>
    internal static JsonNode? Nested(FormatProject project, TypeRef type, Random random, int depth) =>
        new RandomValues(project, random).Generate(type, null, null, depth);

    private JsonNode? Generate(TypeRef type, FieldDef? field, StructScope? scope, int depth)
    {
        var deep = depth >= MaxDepth;
        switch (type)
        {
            case PrimitiveRef primitive:
                return Primitive(primitive.Kind, field);
            case StringRef str:
                return JsonValue.Create(RandomString(str, field, scope, deep));
            case BytesRef bytes:
            {
                var buffer = new byte[Fit(Count(bytes.Length, scope, deep, field?.MaxLength), bytes.Length, 1)];
                _random.NextBytes(buffer);
                return JsonValue.Create(Hex.Format(buffer));
            }
            case ArrayRef { Length.Kind: LengthKind.Until } until:
                return UntilArray(until, scope, deep, depth);
            case ArrayRef array:
            {
                var count = Count(array.Length, scope, deep, field?.MaxItems, field?.MinItems);
                var items = new JsonArray();
                for (var i = 0; i < count; i++) items.Add(Generate(array.Element, null, scope, depth + 1));
                return items;
            }
            case MapRef map:
            {
                var count = Count(map.Length, scope, deep, field?.MaxItems);
                var result = new JsonArray();
                for (var i = 0; i < count; i++)
                    result.Add(new JsonObject { ["key"] = Generate(map.Key, null, scope, depth + 1), ["value"] = Generate(map.Value, null, scope, depth + 1) });
                return result;
            }
            case OptionalRef optional:
                return deep || _random.Next(3) == 0 ? null : Generate(optional.Inner, field, scope, depth + 1);
            case SizedRef { Fill: not null, Length.Kind: LengthKind.Fixed } filled when _project.Unalias(filled.Inner) is StringRef or BytesRef:
            {
                // A string or bytes in a filled region: short enough to fit, with its length prefix.
                _room = filled.Length.Fixed;
                try
                {
                    return Generate(filled.Inner, field, scope, depth + 1);
                }
                finally
                {
                    _room = null;
                }
            }
            case SizedRef sized:
                return Generate(sized.Inner, field, scope, depth + 1);
            case FixedPointRef fixedPoint:
            {
                var raw = Primitive(fixedPoint.Base.Kind, null);
                return JsonValues.FromDouble((double)JsonValues.Number(raw)!.Value / Math.Pow(2, fixedPoint.FractionBits));
            }
            case NativeRef native:
            {
                var bound = _project.Bind(native);
                return depth >= MaxDepth ? bound.Type.DefaultValue(bound.Args) : bound.Type.RandomValue(bound.Args, _random, depth);
            }
            case NamedRef named:
                return Named(named, field, scope, depth);
            default:
                return null;
        }
    }

    /// <summary>A number in the field's min/max, else between -1000 and 1000.</summary>
    private double RandomFloat(FieldDef? field)
    {
        var min = field?.Min is { } fieldMin ? (double)fieldMin : -1000;
        var max = field?.Max is { } fieldMax ? (double)fieldMax : Math.Max(min, 0) + 1000;
        if (field?.Min == null && max < min) min = max - 1000;
        return min + _random.NextDouble() * (max - min);
    }

    private JsonNode? Primitive(PrimitiveKind kind, FieldDef? field)
    {
        switch (kind)
        {
            case PrimitiveKind.Bool:
                return JsonValue.Create(_random.Next(2) == 1);
            case PrimitiveKind.F16:
                return JsonValue.Create((double)(Half)RandomFloat(field));
            case PrimitiveKind.F32:
                return JsonValue.Create((double)(float)RandomFloat(field));
            case PrimitiveKind.F64:
                return JsonValue.Create(RandomFloat(field));
            case PrimitiveKind.Uuid:
                return JsonValue.Create(RandomUuid());
        }

        var (min, max) = Primitives.Range(kind);
        if (field?.Min is { } fieldMin && fieldMin > min) min = Math.Ceiling(fieldMin);
        if (field?.Max is { } fieldMax && fieldMax < max) max = Math.Floor(fieldMax);
        if (min > max) return JsonValues.FromInteger(min);

        // Favour small values and the edges of the range, which are the interesting cases for varints.
        decimal value = _random.Next(6) switch
        {
            0 => min,
            1 => max,
            2 => Math.Clamp(0, min, max),
            _ => Between(Math.Max(min, -1000), Math.Min(max, 1000)),
        };
        return JsonValues.FromInteger(value);
    }

    private decimal Between(decimal min, decimal max)
    {
        if (min > max) return min;
        return min + Math.Floor((decimal)_random.NextDouble() * (max - min + 1));
    }

    private string RandomUuid()
    {
        var bytes = new byte[16];
        _random.NextBytes(bytes);
        var hex = Hex.Format(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private int Count(LengthSpec length, StructScope? scope, bool deep, int? max, int? min = null)
    {
        switch (length.Kind)
        {
            case LengthKind.Fixed:
                return length.Fixed;
            case LengthKind.Expression when scope != null && !UsesComputed(length.Expression!, scope.Definition):
                try
                {
                    return (int)_context.Evaluator.EvaluateLong(Expr.Parse(length.Expression!), scope);
                }
                catch (ExprException)
                {
                    // The length depends on something generated later (e.g. a computed field): any count works.
                    break;
                }
        }

        if (deep) return min ?? 0;
        var upper = Math.Min(max ?? MaxItems, MaxItems);
        return _random.Next(Math.Min(min ?? 0, upper), upper + 1);
    }

    /// <summary>The bytes a value in a filled region may take (<see cref="Fit"/>), or null outside one.</summary>
    private int? _room;

    /// <summary>A count of units of <paramref name="unitSize"/> bytes, at most what fits the filled region with its length prefix.</summary>
    private int Fit(int count, LengthSpec length, int unitSize)
    {
        if (_room is not { } room || length.Kind == LengthKind.Fixed) return count;
        if (length.Kind is LengthKind.Default or LengthKind.Prefix) room -= Primitives.MinWireSize((length.Prefix ?? _project.Defaults.Length).Kind);
        return Math.Clamp(room / unitSize, 0, count);
    }

    private string RandomString(StringRef str, FieldDef? field, StructScope? scope, bool deep)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";
        var count = str.Length.Kind == LengthKind.Expression
            ? Count(str.Length, scope, deep, null)
            : _random.Next(field?.MinLength ?? 0, Math.Min(field?.MaxLength ?? 12, 12) + 1);
        if (str.Length.Kind == LengthKind.Fixed) count = str.Length.Fixed;
        count = Fit(count, str.Length, Strings.UnitSize(str.Encoding ?? _project.Defaults.String));
        var letters = alphabet;
        if (str.Padding != StringPadding.None)
        {
            // Any length that fits the field, without the pad character (which would be dropped when read).
            count = _random.Next(count / Strings.UnitSize(str.Encoding ?? _project.Defaults.String) + 1);
            letters = alphabet.Replace(str.Pad.ToString(), "");
        }

        var builder = new StringBuilder(count);
        for (var i = 0; i < count; i++) builder.Append(letters[_random.Next(letters.Length)]);
        return builder.ToString();
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
                if (enumDef.Members.Count == 0) return JsonValue.Create(0);
                return JsonValue.Create(enumDef.Members[_random.Next(enumDef.Members.Count)].Name);
            case VariantDef variant:
            {
                if (variant.Cases.Count == 0) return null;
                var variantCase = variant.Cases[_random.Next(variant.Cases.Count)];
                if (named.Tag != null && scope != null)
                {
                    // External tag: pick the case selected by the earlier field, or set that field to match the case.
                    try
                    {
                        variantCase = _context.FindCase(variant, _context.Evaluator.Evaluate(Expr.Parse(named.Tag), scope)) ?? variantCase;
                    }
                    catch (ExprException)
                    {
                    }

                    if (Expr.Parse(named.Tag) is NameExpr tagField && scope.Definition.Fields.FirstOrDefault(f => f.Name == tagField.Name) is
                            { Computed: null, Const: null } tagFieldDef)
                    {
                        scope.Value[tagField.Name] = _context.CaseTag(variant, variantCase) switch
                        {
                            string text => JsonValue.Create(text),
                            long number when _project.Unalias(tagFieldDef.Type) is NamedRef tagRef && _project.Find(tagRef.Name) is EnumDef e
                                && e.FindByValue(number) is { } member => JsonValue.Create(member.Name),
                            long number => JsonValues.FromInteger(number),
                            _ => JsonValue.Create(0),
                        };
                    }
                }

                var value = variantCase.Type == null ? null : Generate(variantCase.Type, null, scope, depth + 1);
                return new JsonObject { [variantCase.Name] = value };
            }
            case BitfieldDef bitfield:
            {
                var obj = new JsonObject();
                foreach (var member in bitfield.Members)
                {
                    var bits = Math.Min(member.Bits, 62);
                    var max = member.Signed ? (1L << (bits - 1)) - 1 : (1L << bits) - 1;
                    var min = member.Signed ? -(1L << (bits - 1)) : 0;
                    obj[member.Name] = member.Type switch
                    {
                        null => JsonValue.Create(_random.NextInt64(min, max + 1)),
                        "bool" => JsonValue.Create(_random.Next(2) == 1),
                        var enumName => _project.Find(enumName) is EnumDef { Members.Count: > 0 } e
                            ? JsonValue.Create(e.Members.Where(m => m.Value >= min && m.Value <= max).Select(m => m.Name).DefaultIfEmpty(e.Members[0].Name)
                                .ElementAt(0))
                            : JsonValue.Create(0),
                    };
                }

                return obj;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Elements for which the end condition is false, then one for which it is true. Elements are drawn at random until
    /// one fits, so a condition that is rarely true (e.g. a sentinel byte) takes a few hundred attempts.
    /// </summary>
    private JsonArray UntilArray(ArrayRef array, StructScope? scope, bool deep, int depth)
    {
        var structDef = (StructDef)_project.Find(((NamedRef)_project.Unalias(array.Element)).Name)!;
        var condition = Expr.Parse(array.Length.Expression!);
        var items = new JsonArray();
        var before = deep ? 0 : _random.Next(MaxItems);
        for (var i = 0; i <= before; i++)
        {
            var last = i == before;
            JsonNode? item = null;
            for (var attempt = 0; attempt < 2000 && item == null; attempt++)
            {
                var candidate = Generate(array.Element, null, scope, depth + 1);
                if (candidate is JsonObject obj && Ends(obj) == last) item = candidate;
            }

            if (item != null) items.Add(item);
            else if (!last) i = before - 1;
        }

        return items;

        bool Ends(JsonObject element)
        {
            try
            {
                return _context.Evaluator.EvaluateBool(condition, new StructScope(_context, structDef, element, scope));
            }
            catch (ExprException)
            {
                return false;
            }
        }
    }

    private JsonObject Struct(StructDef structDef, StructScope? parent, int depth)
    {
        var obj = new JsonObject();
        var scope = new StructScope(_context, structDef, obj, parent);
        var lengthFields = LengthFields(structDef);
        var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in structDef.Fields)
        {
            if (field.Const != null) continue;
            if (field.Computed != null) continue;
            if (field.If != null)
            {
                bool present;
                try
                {
                    // A condition on a computed field (e.g. "hasX" = "x != null") is decided here; encoding computes it.
                    // Fields with the same condition get the same decision.
                    if (!UsesComputed(field.If, structDef)) present = _context.Evaluator.EvaluateBool(Expr.Parse(field.If), scope);
                    else if (!decisions.TryGetValue(field.If, out present)) decisions[field.If] = present = _random.Next(2) == 0;
                }
                catch (ExprException)
                {
                    present = _random.Next(2) == 0;
                }

                if (!present) continue;
            }

            obj[field.Name] = lengthFields.Contains(field.Name) && Primitives.IsInteger((_project.Unalias(field.Type) as PrimitiveRef)?.Kind ?? PrimitiveKind.Bool)
                ? JsonValue.Create(depth + 1 >= MaxDepth ? 0 : _random.Next(MaxItems + 1))
                : Generate(field.Type, field, scope, depth + 1);
        }

        return obj;
    }

    private static bool UsesComputed(string expression, StructDef structDef) =>
        Expr.Parse(expression).Names().Any(name => structDef.Fields.Any(f => f.Name == name && f.Computed != null));

    /// <summary>Fields used in length expressions; their random values are kept small.</summary>
    private static HashSet<string> LengthFields(StructDef structDef)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in structDef.Fields) Collect(field.Type);
        return names;

        void Collect(TypeRef type)
        {
            var length = type switch
            {
                ArrayRef array => array.Length,
                MapRef map => map.Length,
                StringRef str => str.Length,
                BytesRef bytes => bytes.Length,
                SizedRef sized => sized.Length,
                _ => null,
            };
            if (length?.Expression is { } expression && Expr.TryParse(expression, out var parsed, out _))
                names.UnionWith(parsed!.Names());
            if (type is OptionalRef optional) Collect(optional.Inner);
            if (type is SizedRef inner) Collect(inner.Inner);
        }
    }
}
