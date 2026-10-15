using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen;

/// <summary>A definition can't be generated; reported as an error of that definition.</summary>
internal sealed class CodegenException(string message) : Exception(message);

/// <summary>
/// The language-independent part of the generators: walks the definitions, reports errors per definition, and
/// translates DSL expressions into the target language through a few syntax hooks. Each language derives from it and
/// emits the declarations, the reading and the writing code.
/// </summary>
internal abstract class Emitter(FormatProject project, IReadOnlySet<string> exclude)
{
    private int _temp;

    protected FormatProject Project => project;
    protected GeneratedOutput Output { get; } = new();

    public GeneratedOutput Run()
    {
        foreach (var definition in Generated.OrderBy(d => d.QualifiedName, StringComparer.Ordinal))
        {
            if (IsExcluded(definition) || definition is AliasDef) continue;
            // Temporaries are numbered per file, so that a change to one type doesn't renumber the others.
            _temp = 0;
            Current = definition;
            try
            {
                var code = definition switch
                {
                    StructDef structDef => Struct(structDef),
                    EnumDef enumDef => Enum(enumDef),
                    VariantDef variant => Variant(variant),
                    BitfieldDef bitfield => Bitfield(bitfield),
                    _ => throw new CodegenException($"Unsupported definition {definition.Kind}"),
                };
                Output.Files.Add(new GeneratedFile(PathOf(definition), code, definition.QualifiedName));
            }
            catch (Exception e) when (e is CodegenException or ExprException)
            {
                Output.Diagnostics.Error($"{definition.QualifiedName}: {e.Message}", definition.Source);
            }
        }

        Current = null;
        Finish();
        return Output;
    }

    /// <summary>The path of a definition's file inside the output folder.</summary>
    protected abstract string PathOf(TypeDef definition);

    protected abstract string Struct(StructDef structDef);
    protected abstract string Enum(EnumDef enumDef);
    protected abstract string Variant(VariantDef variant);
    protected abstract string Bitfield(BitfieldDef bitfield);

    /// <summary>Adds the files that don't belong to a definition (registry, runtime).</summary>
    protected virtual void Finish()
    {
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The definition whose file is being generated; null for shared files (registry, runtime).</summary>
    protected TypeDef? Current { get; private set; }

    /// <summary>
    /// Types written by hand or coming from elsewhere (skipped libraries): referenced, but not generated (and aliases
    /// among them are not inlined). <paramref name="reference"/> is a type name as written in the definitions.
    /// </summary>
    protected bool IsExcluded(string reference) => IsExcluded(Definition(reference));

    protected bool IsExcluded(TypeDef definition) => exclude.Contains(definition.QualifiedName);

    /// <summary>How the project uses the library of a definition, or null for the project's own definitions.</summary>
    protected LibraryDependency? DependencyOf(TypeDef definition) => definition.Owner is { } owner
        ? project.Settings.Dependencies.FirstOrDefault(d => d.Library == owner.Name)
        : null;

    private List<TypeDef>? _generated;

    /// <summary>
    /// The definitions code is generated for: the project's own, and the library definitions they use (directly or through
    /// other library definitions), except those of skipped libraries.
    /// </summary>
    protected IReadOnlyList<TypeDef> Generated => _generated ??= CollectGenerated();

    private List<TypeDef> CollectGenerated()
    {
        var result = new List<TypeDef>();
        var seen = new HashSet<TypeDef>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<TypeDef>(project.Types.Values);
        while (queue.TryDequeue(out var definition))
        {
            if (!seen.Add(definition) || definition.Owner != null && IsExcluded(definition)) continue;
            result.Add(definition);
            foreach (var name in TypeReferences.Of(definition))
                if (project.Find(name) is { Owner: not null } used) queue.Enqueue(used);
        }

        return result;
    }

    protected TypeDef Definition(string name) =>
        project.Find(name) ?? throw new CodegenException($"Unknown type '{name}'");

    /// <summary>A fresh local name; numbered per file.</summary>
    protected string Temp(string name) => $"__{name}{_temp++}";

    protected static string Describe(TypeRef type) => TypeExpressions.TryFormat(type) ?? type.ToString();

    /// <summary>The least number of bytes a value takes on the wire, to check counts before allocating.</summary>
    protected int MinSize(TypeRef type) => project.Unalias(type) switch
    {
        PrimitiveRef primitive => Primitives.MinWireSize(primitive.Kind),
        FixedPointRef fixedPoint => Primitives.MinWireSize(fixedPoint.Base.Kind),
        NamedRef { As: { } wire } => Primitives.MinWireSize(wire.Kind),
        NamedRef named when Definition(named.Name) is EnumDef enumDef => Primitives.MinWireSize(enumDef.Type.Kind),
        _ => 0,
    };

    protected bool HasEndian(PrimitiveRef primitive) => Primitives.HasEndian(primitive.Kind);

    protected Endian EndianOf(PrimitiveRef primitive) => primitive.Endian ?? project.Defaults.Endian;

    /// <summary>
    /// The name part of the runtime's read and write methods, shared by all runtimes: <c>U8</c>, <c>I16LE</c>,
    /// <c>VarUInt32</c>, <c>UuidBE</c>, ...
    /// </summary>
    protected string Method(PrimitiveRef primitive)
    {
        var endian = HasEndian(primitive) ? EndianOf(primitive) == Endian.Big ? "BE" : "LE" : "";
        return primitive.Kind switch
        {
            PrimitiveKind.Bool => "Bool",
            PrimitiveKind.U8 => "U8",
            PrimitiveKind.I8 => "I8",
            PrimitiveKind.VarUInt32 => "VarUInt32",
            PrimitiveKind.VarUInt64 => "VarUInt64",
            PrimitiveKind.VarInt32 => "VarInt32",
            PrimitiveKind.VarInt64 => "VarInt64",
            PrimitiveKind.ZigZag32 => "ZigZag32",
            PrimitiveKind.ZigZag64 => "ZigZag64",
            PrimitiveKind.Uuid => "Uuid" + endian,
            var kind => kind.ToString().ToUpperInvariant() + endian,
        };
    }

    /// <summary>
    /// The packet registries: one per protocol of the project, or a single one (with a null protocol) over all packets
    /// when the project declares none. Packets are ordered by id; packets sharing an id are reported and the first one
    /// wins. Protocols whose name would clash in the generated code are reported and left out.
    /// </summary>
    /// <param name="registryClass">The name of the class holding the registries.</param>
    protected List<(ProtocolDef? Protocol, List<StructDef> Packets)> Registries(string registryClass)
    {
        if (project.Settings.Protocols.Count == 0)
            return [(null, RegistryPackets(project.Packets, null))];

        var registries = new List<(ProtocolDef?, List<StructDef>)>();
        foreach (var protocol in project.Settings.Protocols.DistinctBy(p => p.Name))
        {
            if (protocol.Name == registryClass || project.Find(protocol.Name) != null || !IsIdentifier(protocol.Name))
            {
                Output.Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"Protocol {protocol.Name}: the name can't be used for a registry class (it is the registry's or a definition's name, or not an identifier)")
                {
                    Area = Diagnostic.ProtocolsArea,
                });
                continue;
            }

            registries.Add((protocol, RegistryPackets(project.PacketsOf(protocol), protocol)));
        }

        return registries;
    }

    private List<StructDef> RegistryPackets(IEnumerable<StructDef> candidates, ProtocolDef? protocol)
    {
        var packets = candidates.Where(p => p.Id != null)
            .OrderBy(p => p.Id).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        foreach (var duplicate in packets.GroupBy(p => p.Id).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ", duplicate.Select(p => p.Name));
            Output.Diagnostics.Add(new Diagnostic(Severity.Warning, protocol == null
                ? $"Packets {names} share id {duplicate.Key}; only the first is dispatched (declare protocols in the project file to dispatch them separately)"
                : $"Packets {names} share id {duplicate.Key} in protocol {protocol.Name}; only the first is dispatched")
            {
                Area = Diagnostic.ProtocolsArea,
            });
        }

        return packets.DistinctBy(p => p.Id).ToList();
    }

    /// <summary>A sentence for the documentation of a protocol's registry.</summary>
    protected static string ProtocolRemark(ProtocolDef protocol, int packets)
    {
        var count = packets == 1 ? "1 packet" : $"{packets} packets";
        return protocol.Groups.Count == 0 ? $"{count}." : $"{count}, those in the groups {string.Join(", ", protocol.Groups)}.";
    }

    private static bool IsIdentifier(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>The wire type of an integer or enum tag that is stored in the variant itself; null for string and external tags.</summary>
    protected PrimitiveRef? TagWire(VariantDef variant) => variant.Tag switch
    {
        null => null,
        NamedRef named when Definition(named.Name) is EnumDef enumDef => named.As ?? enumDef.Type,
        var tag => project.Unalias(tag) as PrimitiveRef,
    };

    /// <summary>The enum that a variant's tags are members of, or null.</summary>
    protected EnumDef? TagEnum(VariantDef variant) =>
        variant.Tag is NamedRef named && Definition(named.Name) is EnumDef enumDef ? enumDef : null;

    // ------------------------------------------------------------------ cycles

    /// <summary>Types that contain themselves by value (directly or through other types), by qualified name.</summary>
    protected HashSet<string> SelfContainingTypes()
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var definition in Generated)
        {
            var targets = new List<string>();
            switch (definition)
            {
                case StructDef structDef:
                    foreach (var field in structDef.Fields) targets.AddRange(ByValue(field.Type));
                    break;
                case VariantDef variant:
                    foreach (var variantCase in variant.Cases.Where(c => c.Type != null)) targets.AddRange(ByValue(variantCase.Type!));
                    break;
            }

            edges[definition.QualifiedName] = targets;
        }

        // Tarjan's strongly connected components.
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);

        void Connect(string node)
        {
            indices[node] = lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? [])
            {
                if (!edges.ContainsKey(next)) continue;
                if (!indices.ContainsKey(next))
                {
                    Connect(next);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                }
                else if (onStack.Contains(next))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                }
            }

            if (lowLinks[node] != indices[node]) return;
            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != node);

            if (component.Count > 1 || edges[node].Contains(node)) result.UnionWith(component);
        }

        foreach (var node in edges.Keys.Order(StringComparer.Ordinal))
            if (!indices.ContainsKey(node)) Connect(node);
        return result;
    }

    private IEnumerable<string> ByValue(TypeRef type)
    {
        switch (type)
        {
            case OptionalRef optional:
                return ByValue(optional.Inner);
            case SizedRef sized:
                return ByValue(sized.Inner);
            case NamedRef named when project.Find(named.Name) is AliasDef alias:
                return ByValue(alias.Target).Append(alias.QualifiedName);
            case NamedRef named:
                return project.Find(named.Name) is { } target ? [target.QualifiedName] : [];
            default:
                return [];
        }
    }

    // ------------------------------------------------------------------ expressions

    protected sealed class Context
    {
        public string Reader { get; init; } = "reader";
        public string Writer { get; init; } = "writer";
        public required Func<string, Value?> Resolve { get; init; }
        public FieldDef? Field { get; init; }

        /// <summary>The same names and streams, for the elements of a collection (no field).</summary>
        public Context Element() => new() { Reader = Reader, Writer = Writer, Resolve = Resolve };

        public Context WithReader(string reader) => new() { Reader = reader, Writer = Writer, Resolve = Resolve, Field = Field };

        public Context WithWriter(string writer) => new() { Reader = Reader, Writer = writer, Resolve = Resolve, Field = Field };

        public static Context Empty => new() { Resolve = _ => null };
    }

    protected enum ValueKind
    {
        Bool,
        Number,
        Float,
        Text,
        Null,
        Other,
    }

    /// <param name="Code">The value converted for arithmetic (a 64-bit integer, a double or a bool).</param>
    /// <param name="Raw">The plain access, for null checks, members and len().</param>
    /// <param name="MayBeNull">Optional or conditional: len() and member access must check for null.</param>
    protected sealed record Value(string Code, string Raw, ValueKind Kind, TypeRef? Type, bool MayBeNull = false);

    /// <summary>The name of a field or case in the generated type.</summary>
    protected abstract string MemberName(string name, string owner);

    /// <summary>An integer literal of the 64-bit type expressions are evaluated in.</summary>
    protected abstract string NumberLiteral(decimal value);

    protected abstract string StringLiteral(string text);

    protected abstract Value EnumConstant(EnumDef enumDef, string member);

    /// <summary>
    /// Converts a field access for arithmetic: integers and enums to a 64-bit integer, floats to double. A value that may
    /// be null counts as 0 (or false).
    /// </summary>
    /// <param name="declared">The type of the value (the inner type of an optional).</param>
    protected abstract string Convert(string access, ValueKind kind, TypeRef declared, bool mayBeNull);

    protected abstract string IndexAccess(string target, string index);

    /// <summary><c>len(x)</c> as a 64-bit integer; null counts as empty.</summary>
    protected abstract string Length(Value value);

    /// <summary><c>bytelen(x)</c>: the UTF-8 length of a string as a 64-bit integer.</summary>
    protected abstract string ByteLength(Value value);

    /// <summary><c>tagof(x)</c> of a variant with integer or enum tags.</summary>
    protected abstract string TagNumber(Value value);

    /// <summary><c>tagof(x)</c> of a variant with string tags.</summary>
    protected abstract string TagText(VariantDef variant, Value value);

    /// <summary>Text (in)equality, <paramref name="op"/> is <c>==</c> or <c>!=</c>.</summary>
    protected abstract string TextCompare(string left, string right, string op);

    /// <param name="conditional">The field has a condition, so it may be missing.</param>
    protected Value FieldValue(string access, TypeRef type, bool conditional)
    {
        var resolved = project.Unalias(type);
        var declared = type;
        var mayBeNull = conditional;
        if (resolved is OptionalRef optional)
        {
            declared = optional.Inner;
            resolved = project.Unalias(optional.Inner);
            mayBeNull = true;
        }

        var kind = resolved switch
        {
            PrimitiveRef { Kind: PrimitiveKind.Bool } => ValueKind.Bool,
            PrimitiveRef { Kind: PrimitiveKind.F32 or PrimitiveKind.F64 } or FixedPointRef => ValueKind.Float,
            PrimitiveRef { Kind: PrimitiveKind.Uuid } => ValueKind.Other,
            PrimitiveRef => ValueKind.Number,
            NamedRef named when Definition(named.Name) is EnumDef => ValueKind.Number,
            StringRef => ValueKind.Text,
            _ => ValueKind.Other,
        };
        var code = kind is ValueKind.Bool or ValueKind.Number or ValueKind.Float ? Convert(access, kind, declared, mayBeNull) : access;
        return new Value(code, access, kind, type, mayBeNull);
    }

    protected Value Translate(Expr expression, Context context)
    {
        switch (expression)
        {
            case LiteralExpr { Value: null }:
                return new Value("null", "null", ValueKind.Null, null);
            case LiteralExpr { Value: bool b }:
                return new Value(b ? "true" : "false", b ? "true" : "false", ValueKind.Bool, null);
            case LiteralExpr { Value: decimal d }:
                var number = NumberLiteral(d);
                return new Value(number, number, ValueKind.Number, null);
            case LiteralExpr { Value: double f }:
                var text = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                return new Value(text, text, ValueKind.Float, null);
            case LiteralExpr { Value: string s }:
                return new Value(StringLiteral(s), StringLiteral(s), ValueKind.Text, null);
            case NameExpr { Name: "_parent" or "_root" }:
                throw new CodegenException("_parent and _root are not supported by the generators");
            case NameExpr name:
                return context.Resolve(name.Name) ?? throw new CodegenException($"Unknown name '{name.Name}' in an expression");
            case EnumConstExpr constant:
                if (Definition(constant.Enum) is not EnumDef enumDef || enumDef.FindByName(constant.Member) == null)
                    throw new CodegenException($"Unknown enum constant {constant}");
                return EnumConstant(enumDef, constant.Member);
            case MemberExpr memberExpr:
            {
                var target = Translate(memberExpr.Target, context);
                if (target.Type == null || project.Unalias(target.Type) is not NamedRef named || Definition(named.Name) is not StructDef structDef)
                    throw new CodegenException($"Can't access '{memberExpr.Name}' in {memberExpr}");
                var field = structDef.Fields.FirstOrDefault(f => f.Name == memberExpr.Name && f.IsInput)
                    ?? throw new CodegenException($"{structDef.Name} has no field '{memberExpr.Name}'");
                return FieldValue($"{target.Raw}.{MemberName(field.Name, structDef.Name)}", field.Type, field.If != null);
            }
            case IndexExpr indexExpr:
            {
                var target = Translate(indexExpr.Target, context);
                var index = Translate(indexExpr.Index, context);
                if (target.Type == null || project.Unalias(target.Type) is not ArrayRef array)
                    throw new CodegenException($"Can't index {indexExpr.Target}");
                return FieldValue(IndexAccess(target.Raw, $"(int){index.Code}"), array.Element, false);
            }
            case UnaryExpr unary:
            {
                var operand = Translate(unary.Operand, context);
                return unary.Operator switch
                {
                    "!" => new Value($"!{Bool(operand)}", "", ValueKind.Bool, null),
                    "-" => new Value($"(-{operand.Code})", "", operand.Kind == ValueKind.Float ? ValueKind.Float : ValueKind.Number, null),
                    _ => new Value($"(~{operand.Code})", "", ValueKind.Number, null),
                };
            }
            case BinaryExpr binary:
                return Binary(binary, context);
            case ConditionalExpr conditional:
            {
                var then = Translate(conditional.Then, context);
                var otherwise = Translate(conditional.Else, context);
                var code = $"({Bool(Translate(conditional.Condition, context))} ? {then.Code} : {otherwise.Code})";
                return new Value(code, code, then.Kind == ValueKind.Null ? otherwise.Kind : then.Kind, then.Type ?? otherwise.Type);
            }
            case CallExpr call:
            {
                if (call.Arguments.Count != 1) throw new CodegenException($"{call.Function}() takes one argument");
                var argument = Translate(call.Arguments[0], context);
                if (call.Function == "tagof" && argument.Type != null && project.Unalias(argument.Type) is NamedRef tagged
                    && Definition(tagged.Name) is VariantDef taggedVariant && project.TagKind(taggedVariant) == VariantTagKind.String)
                {
                    var tagText = TagText(taggedVariant, argument);
                    return new Value(tagText, tagText, ValueKind.Text, null);
                }

                var code = call.Function switch
                {
                    "len" => Length(argument),
                    "bytelen" => ByteLength(argument),
                    "tagof" => TagNumber(argument),
                    _ => throw new CodegenException($"Unknown function {call.Function}()"),
                };
                return new Value(code, code, ValueKind.Number, null);
            }
            default:
                throw new CodegenException($"Unsupported expression {expression}");
        }
    }

    private Value Binary(BinaryExpr binary, Context context)
    {
        var left = Translate(binary.Left, context);
        var right = Translate(binary.Right, context);
        var op = binary.Operator;
        switch (op)
        {
            case "==" or "!=" when left.Kind == ValueKind.Null || right.Kind == ValueKind.Null:
                return new Value($"({left.Raw} {op} {right.Raw})", "", ValueKind.Bool, null);
            case "==" or "!=" when left.Kind == ValueKind.Bool && right.Kind == ValueKind.Bool:
            case "&" or "|" or "^" when left.Kind == ValueKind.Bool && right.Kind == ValueKind.Bool:
                return new Value($"({left.Code} {op} {right.Code})", "", ValueKind.Bool, null);
            case "==" or "!=" when left.Kind == ValueKind.Text || right.Kind == ValueKind.Text:
                return new Value(TextCompare(left.Raw, right.Raw, op), "", ValueKind.Bool, null);
            case "&&" or "||":
                return new Value($"({Bool(left)} {op} {Bool(right)})", "", ValueKind.Bool, null);
            case "==" or "!=" or "<" or "<=" or ">" or ">=":
                return new Value($"({Numeric(left)} {op} {Numeric(right)})", "", ValueKind.Bool, null);
            case "<<" or ">>":
                return new Value($"({Numeric(left)} {op} (int){Numeric(right)})", "", ValueKind.Number, null);
            case "+" when left.Kind == ValueKind.Text || right.Kind == ValueKind.Text:
                return new Value($"({left.Raw} + {right.Raw})", "", ValueKind.Text, null);
            default:
                var kind = left.Kind == ValueKind.Float || right.Kind == ValueKind.Float ? ValueKind.Float : ValueKind.Number;
                if (kind == ValueKind.Float && op is "&" or "|" or "^") throw new CodegenException($"Operator {op} needs integers");
                var code = $"({Numeric(left)} {op} {Numeric(right)})";
                return new Value(code, code, kind, null);
        }
    }

    protected static string Bool(Value value) => value.Kind switch
    {
        ValueKind.Bool => value.Code,
        ValueKind.Number or ValueKind.Float => $"({value.Code} != 0)",
        ValueKind.Null => "false",
        _ => $"({value.Raw} != null)",
    };

    protected static string Numeric(Value value) => value.Kind == ValueKind.Bool ? $"({value.Code} ? 1L : 0L)" : value.Code;

    /// <summary>An expression as a bool, or as a 64-bit integer or double (see <paramref name="kind"/>).</summary>
    protected string Expression(string text, Context context, out ValueKind kind)
    {
        var value = Translate(Expr.Parse(text), context);
        kind = value.Kind;
        return value.Kind == ValueKind.Bool ? value.Code : Numeric(value);
    }

    protected string Condition(string text, Context context) => Bool(Translate(Expr.Parse(text), context));

    // ------------------------------------------------------------------ scopes

    /// <summary>Names while reading a struct: the fields read so far.</summary>
    protected Func<string, Value?> ReadScope(IReadOnlyDictionary<string, FieldDef> known, Func<FieldDef, string> access) =>
        name => known.TryGetValue(name, out var field) ? FieldValue(access(field), field.Type, field.If != null) : null;

    /// <summary>
    /// Names while writing a struct: stored fields, constants and the computed fields that are calculated already.
    /// </summary>
    protected Func<string, Value?> WriteScope(StructDef structDef, IReadOnlySet<string> computedDone, Func<FieldDef, string> access,
        Func<FieldDef, string> constant)
    {
        var fields = structDef.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return name =>
        {
            if (!fields.TryGetValue(name, out var field)) return null;
            if (field.Const != null) return FieldValue($"({constant(field)})", field.Type, false);
            if (field.Computed != null && !computedDone.Contains(name))
                throw new CodegenException($"Computed field '{name}' is used before it is calculated");
            return FieldValue(access(field), field.Type, field.If != null);
        };
    }

    /// <summary>Names in the end condition of an <c>until</c> array are the fields of the element.</summary>
    protected Context ElementContext(ArrayRef array, string item)
    {
        if (project.Unalias(array.Element) is not NamedRef named || Definition(named.Name) is not StructDef structDef)
            throw new CodegenException($"An array with 'until' needs struct elements, not {Describe(array.Element)}");
        return new Context
        {
            Resolve = n => structDef.Fields.FirstOrDefault(f => f.Name == n) switch
            {
                null => null,
                { IsInput: false } => throw new CodegenException($"The until condition of {structDef.Name}[] uses '{n}', which is not stored in {structDef.Name}"),
                var f => FieldValue($"{item}.{MemberName(f.Name, structDef.Name)}", f.Type, f.If != null),
            },
        };
    }
}
