using FormatStudio.Codegen;
using FormatStudio.Codegen.Java;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;

namespace FormatStudio.Minecraft.Netty;

/// <summary>
/// The java-stream code with a <c>CODEC</c> in every type: <c>StreamCodec.composite</c> over the fields' codecs where a
/// struct allows it (<see cref="CompositeProblem"/>), else <c>NettyFormat.codec</c> over the generated read and write
/// methods. Types that only have a codec (bindings) are read and written through <c>NettyFormat.decode</c>/<c>encode</c>.
/// Minecraft's classes are imported by their simple names unless a definition has that name.
/// </summary>
internal sealed class NettyEmitter(FormatProject project, JavaOptions options, NettyOptions netty) : JavaEmitter(project, options)
{
    /// <summary>The most fields <c>StreamCodec.composite</c> takes.</summary>
    private const int MaxComposite = 12;

    private const string StreamCodecClass = "net.minecraft.network.codec.StreamCodec";
    private const string ByteBufCodecsClass = "net.minecraft.network.codec.ByteBufCodecs";
    private const string PayloadClass = "net.minecraft.network.protocol.common.custom.CustomPacketPayload";
    private const string IdentifierClass = "net.minecraft.resources.Identifier";
    private const string UuidUtilClass = "net.minecraft.core.UUIDUtil";

    private readonly HashSet<string> _imports = new(StringComparer.Ordinal);

    protected override string Generator => NettyGenerator.GeneratorName;

    // CODEC and TYPE are the members this generator adds; type() is the payload's.
    protected override IReadOnlySet<string> ReservedMembers { get; } = new HashSet<string>(StringComparer.Ordinal) { "CODEC", "TYPE", "type" };

    private string NettyFormat => Name($"{Options.RuntimePackage}.NettyFormat");

    /// <summary>
    /// A class by its simple name, imported, unless that name is taken (a definition, or another class imported in the
    /// file): then by its full name.
    /// </summary>
    private string Name(string fullName)
    {
        var simple = fullName[(fullName.LastIndexOf('.') + 1)..];
        if (Project.Types.ContainsKey(simple) || _imports.Any(i => i != fullName && i.EndsWith("." + simple, StringComparison.Ordinal))) return fullName;
        _imports.Add(fullName);
        return simple;
    }

    protected override IEnumerable<string> FileImports()
    {
        var imports = _imports.ToList();
        _imports.Clear();
        return imports;
    }

    private string CodecType(string valueType) => $"{Name(StreamCodecClass)}<{Name(netty.Buffer)}, {valueType}>";

    protected override void Finish()
    {
        base.Finish();
        if (!Options.IncludeRuntime) return;
        Output.Files.Add(new GeneratedFile($"{PackagePath(Options.RuntimePackage)}/NettyFormat.java",
            $"{JavaGenerator.FileMarker}\n// The Format Studio runtime for Minecraft's StreamCodecs, written by format-studio. Do not edit.\n" +
            NativeTemplate(MinecraftFiles.Text("NettyFormat.java.txt").Replace("\r\n", "\n"), null, new Dictionary<string, string>())));
    }

    protected override void CheckNative(BoundNative bound)
    {
        if (bound.Type == NbtNative.Instance && bound.Args["flavor"] != "java-network")
            throw new CodegenException($"{GeneratorName} reads and writes NBT as Minecraft's network NBT, nbt(java-network), not nbt({bound.Args["flavor"]})");
    }

    private static string GeneratorName => NettyGenerator.GeneratorName;

    protected override string StructInterfaces(StructDef structDef, string interfaceName) =>
        structDef.IsPacket && structDef.TextId != null ? $"{interfaceName}, {Name(PayloadClass)}" : interfaceName;

    protected override string ReadWithCodec(string what, string codec, TypeRef type, CodeWriter w, Context context) =>
        $"{NettyFormat}.decode({codec}, {context.Reader})";

    protected override void WriteWithCodec(string what, string codec, TypeRef type, string value, CodeWriter w, Context context) =>
        w.Line($"{NettyFormat}.encode({codec}, {context.Writer}, {value});");

    // ------------------------------------------------------------------ codecs of the types

    protected override void TypeMembers(TypeDef definition, CodeWriter w)
    {
        var name = TypeName(definition);
        switch (definition)
        {
            case StructDef structDef:
                StructCodec(structDef, name, w);
                if (structDef.IsPacket && structDef.TextId != null) PayloadMembers(name, w);
                break;
            case EnumDef enumDef:
                EnumCodec(enumDef, name, w);
                break;
            case BitfieldDef or VariantDef { Tag: not null }:
                w.Line();
                w.Line("/** Minecraft's StreamCodec of the type, over its read and write methods. */");
                w.Line($"public static final {CodecType(name)} CODEC = {NettyFormat}.codec({name}::read, {name}::write);");
                break;
        }
    }

    private void StructCodec(StructDef structDef, string name, CodeWriter w)
    {
        var fields = structDef.Fields;
        var problem = CompositeProblem(structDef, out var codecs);
        if (problem != null && netty.Strategy == NettyStrategy.Composite)
            throw new CodegenException($"can't be a StreamCodec.composite: {problem}");

        w.Line();
        if (problem != null)
        {
            w.Line("/** Minecraft's StreamCodec of the type, over its read and write methods. */");
            w.Line($"public static final {CodecType(name)} CODEC = {NettyFormat}.codec({name}::read, {name}::write);");
            return;
        }

        w.Line("/** Minecraft's StreamCodec of the type. */");
        if (fields.Count == 0)
        {
            w.Line($"public static final {CodecType(name)} CODEC = {Name(StreamCodecClass)}.unit(new {name}());");
            return;
        }

        w.Line($"public static final {CodecType(name)} CODEC = {Name(StreamCodecClass)}.composite(");
        for (var i = 0; i < fields.Count; i++) w.Line($"        {codecs[i]}, {name}::{FieldMember(fields[i].Name)},");
        w.Line($"        {name}::new);");
    }

    /// <summary>
    /// Why a struct can't be a <c>StreamCodec.composite</c>, or null when it can; then <paramref name="codecs"/> are its
    /// fields' codecs.
    /// </summary>
    private string? CompositeProblem(StructDef structDef, out List<string> codecs)
    {
        codecs = [];
        if (netty.Strategy == NettyStrategy.Manual) return "the strategy is manual";
        if (structDef.Fields.FirstOrDefault(f => !f.IsInput) is { } stored) return $"{stored.Name} is {(stored.Const != null ? "a const" : "computed")}";
        if (structDef.Fields.FirstOrDefault(f => f.If != null) is { } conditional) return $"{conditional.Name} is conditional";
        if (Options.ValidateRanges && structDef.Fields.FirstOrDefault(f => f.Min != null || f.Max != null) is { } ranged)
            return $"{ranged.Name} has a range to check";
        if (structDef.Fields.Count > MaxComposite) return $"it has {structDef.Fields.Count} fields, more than the {MaxComposite} StreamCodec.composite takes";
        // The codec of a type that contains itself would use its own CODEC before it is set.
        if (ContainsItself(structDef)) return "it contains itself";
        foreach (var field in structDef.Fields)
        {
            if (Codec(field.Type, field) is not { } codec) return $"{field.Name} ({Describe(field.Type)}) has no StreamCodec of its own";
            codecs.Add(codec);
        }

        return null;
    }

    private bool ContainsItself(TypeDef start)
    {
        var seen = new HashSet<TypeDef>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<TypeDef>([start]);
        while (stack.TryPop(out var definition))
        {
            foreach (var name in TypeReferences.Of(definition))
            {
                if (Project.Find(name) is not { } used) continue;
                if (ReferenceEquals(used, start)) return true;
                if (seen.Add(used)) stack.Push(used);
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>StreamCodec</c> expression of a type whose values are those of the generated code, or null when there is
    /// none: Minecraft's <c>ByteBufCodecs</c> for the wire formats they write, the <c>CODEC</c> of generated (and
    /// skipped) types, the codec of bindings and native types.
    /// </summary>
    /// <param name="field">The field of the type, for its <c>maxItems</c>; null inside other types.</param>
    private string? Codec(TypeRef type, FieldDef? field)
    {
        var codecs = Name(ByteBufCodecsClass);
        switch (type)
        {
            case PrimitiveRef primitive:
                return PrimitiveCodec(primitive, codecs);
            case StringRef str when (str.Encoding ?? Project.Defaults.String) == StringEncoding.Utf8 && str.Padding == StringPadding.None && IsVarIntLength(str.Length):
                return str.Max is { } max ? $"{codecs}.stringUtf8({max})" : $"{codecs}.STRING_UTF8";
            case BytesRef bytes when IsVarIntLength(bytes.Length):
                return $"{codecs}.BYTE_ARRAY";
            case ArrayRef array when IsVarIntLength(array.Length) && Codec(array.Element, null) is { } element:
                return $"{element}.apply({codecs}.list({field?.MaxItems?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}))";
            case OptionalRef optional when Codec(optional.Inner, null) is { } inner:
                return $"{NettyFormat}.nullable({inner})";
            case NativeRef native:
            {
                var (codegen, args) = Native(native);
                return codegen.Codec is { } codec ? NativeTemplate(codec, args, new Dictionary<string, string>()) : null;
            }
            case NamedRef named when Binding(named) is { } bound:
                return bound.Codec is { } boundCodec ? NativeTemplate(boundCodec, null, new Dictionary<string, string>()) : null;
            case NamedRef { As: null, Tag: null } named:
                return Definition(named.Name) switch
                {
                    AliasDef alias when !IsExcluded(alias) => Codec(alias.Target, field),
                    VariantDef { Tag: null } => null,
                    var definition => $"{TypeName(definition)}.CODEC",
                };
            default:
                return null;
        }
    }

    /// <summary>A length that Minecraft's codecs write: a VarInt prefix (also as the project's default).</summary>
    private bool IsVarIntLength(LengthSpec length) => length.Kind switch
    {
        LengthKind.Default => Project.Defaults.Length.Kind == PrimitiveKind.VarInt32,
        LengthKind.Prefix => length.Prefix!.Kind == PrimitiveKind.VarInt32,
        _ => false,
    };

    /// <summary>The <c>ByteBufCodecs</c> constant of a primitive whose Java type is the one the codec has; null for the others.</summary>
    private string? PrimitiveCodec(PrimitiveRef primitive, string codecs)
    {
        var big = !HasEndian(primitive) || EndianOf(primitive) == Endian.Big;
        return primitive.Kind switch
        {
            PrimitiveKind.Bool => $"{codecs}.BOOL",
            PrimitiveKind.I8 => $"{codecs}.BYTE",
            PrimitiveKind.I16 when big => $"{codecs}.SHORT",
            PrimitiveKind.I32 when big => $"{codecs}.INT",
            PrimitiveKind.I64 when big => $"{codecs}.LONG",
            PrimitiveKind.F32 when big => $"{codecs}.FLOAT",
            PrimitiveKind.F64 when big => $"{codecs}.DOUBLE",
            PrimitiveKind.VarInt32 => $"{codecs}.VAR_INT",
            PrimitiveKind.VarInt64 => $"{codecs}.VAR_LONG",
            // Widened unsigned values are ints.
            PrimitiveKind.U16 when big && !SameSize => $"{codecs}.UNSIGNED_SHORT",
            PrimitiveKind.U8 when !SameSize => $"{codecs}.BYTE.map(b -> b & 0xFF, i -> (byte) (int) i)",
            PrimitiveKind.Uuid when big => $"{Name(UuidUtilClass)}.STREAM_CODEC",
            _ => null,
        };
    }

    /// <summary>
    /// A Java enum gets <c>read</c>/<c>write</c> methods for its codec; an enum kept as numbers (flags, unknown values)
    /// gets static ones and a codec of the numbers.
    /// </summary>
    private void EnumCodec(EnumDef enumDef, string name, CodeWriter w)
    {
        var type = IsOpen(enumDef) ? EnumType(enumDef) : name;
        var reference = new NamedRef(enumDef.QualifiedName);
        w.Line();
        w.Line("/** Reads a value as the enum's wire type. */");
        w.OpenInline($"public static {type} read(FormatReader reader) throws {IOException}");
        w.Line($"return {Read(reference, w, Context.Empty)};");
        w.Close();
        w.Line();
        if (IsOpen(enumDef))
        {
            w.Line("/** Writes a value as the enum's wire type. */");
            w.OpenInline($"public static void write(FormatWriter writer, {type} value) throws {IOException}");
            Write(reference, "value", w, Context.Empty);
            w.Close();
            w.Line();
            w.Line("/** Minecraft's StreamCodec of the values. */");
            w.Line($"public static final {CodecType(Box(type))} CODEC = {NettyFormat}.codec({name}::read, (value, writer) -> write(writer, value));");
            return;
        }

        w.Line("/** Writes the value as the enum's wire type. */");
        w.OpenInline($"public void write(FormatWriter writer) throws {IOException}");
        Write(reference, "this", w, Context.Empty);
        w.Close();
        w.Line();
        w.Line("/** Minecraft's StreamCodec of the enum. */");
        w.Line($"public static final {CodecType(name)} CODEC = {NettyFormat}.codec({name}::read, {name}::write);");
    }

    /// <summary>A packet with a text id is a <c>CustomPacketPayload</c> whose type has the id.</summary>
    private void PayloadMembers(string name, CodeWriter w)
    {
        var payload = Name(PayloadClass);
        w.Line();
        w.Line("/** The payload type, with the packet id. */");
        w.Line($"public static final {payload}.Type<{name}> TYPE = new {payload}.Type<>({Name(IdentifierClass)}.parse(ID));");
        w.Line();
        w.Line("@Override");
        w.OpenInline($"public {payload}.Type<{name}> type()");
        w.Line("return TYPE;");
        w.Close();
    }
}
