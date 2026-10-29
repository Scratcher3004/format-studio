using System.Globalization;
using System.Text;
using System.Text.Json;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen.Java;

/// <summary>
/// Java syntax for the <see cref="CodeEmitter"/>. Every type is a class, so types that contain themselves need no special
/// handling. Unsigned values are widened (u8/u16 to int, u32 to long, 64-bit values keep their bits in a long) or use the
/// signed type of the same size (<see cref="JavaUnsigned"/>); the runtime always reads and writes the widened types.
/// Arrays are <c>java.util.List</c>s or Java arrays, maps are lists of entries; conditional and optional values use the
/// boxed types and null.
/// </summary>
internal sealed class JavaEmitter(FormatProject project, JavaOptions options) : CodeEmitter(project, options.Exclude)
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do",
        "double", "else", "enum", "extends", "false", "final", "finally", "float", "for", "goto", "if", "implements", "import",
        "instanceof", "int", "interface", "long", "native", "new", "null", "package", "private", "protected", "public", "return",
        "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "true", "try",
        "void", "volatile", "while", "_", "yield",
    ];

    /// <summary>Field names the generated code itself uses.</summary>
    private static readonly HashSet<string> Reserved = ["ID", "kind"];

    /// <summary>
    /// Names a record component can't have: its accessor would clash with a method of <c>Object</c> or of the packet
    /// interfaces.
    /// </summary>
    private static readonly HashSet<string> RecordReserved = ["clone", "finalize", "getClass", "hashCode", "notify", "notifyAll", "packetId", "toString", "wait"];

    /// <summary>Names of the fields and helpers of generated enums, which members can't use.</summary>
    private static readonly HashSet<string> EnumReserved = ["value", "tag", "BY_VALUE", "BY_TAG"];

    private const string IOException = "java.io.IOException";

    private string PackagePath(string package) => package.Replace('.', '/');

    protected override string PathOf(TypeDef definition) => $"{PackagePath(PackageOf(definition))}/{JavaName(definition)}.java";

    protected override string Generator => "java-stream";

    protected override string Marker => JavaGenerator.FileMarker;

    protected override string NativeNamespace => options.Package + ".natives";

    protected override string RuntimeNamespace => options.RuntimePackage;

    protected override string NativeFilePath(string fileName) => $"{PackagePath(NativeNamespace)}/{fileName}";

    protected override void Finish()
    {
        if (Project.Packets.Any()) Output.Files.Add(new GeneratedFile($"{PackagePath(options.Package)}/{options.Registry}.java", Registry()));
        if (!options.IncludeRuntime) return;
        foreach (var (file, source) in JavaGenerator.RuntimeSources())
        {
            var text = source.Replace("package formatstudio.runtime;", $"package {options.RuntimePackage};");
            Output.Files.Add(new GeneratedFile($"{PackagePath(options.RuntimePackage)}/{file}",
                $"{JavaGenerator.FileMarker}\n// The Format Studio runtime for Java, written by format-studio. Do not edit.\n{text}"));
        }
    }

    // ------------------------------------------------------------------ names and types

    private static string Id(string name) => Keywords.Contains(name) ? name + "_" : name;

    /// <summary>The Java field name of a field or case, avoiding generated members.</summary>
    private static string Member(string name) => Reserved.Contains(name) ? name + "Value" : Id(name);

    /// <summary>The Java name of a struct's field: a public field, or with <c>records</c> a record component.</summary>
    private string FieldMember(string name) => options.Records && RecordReserved.Contains(name) ? name + "Value" : Member(name);

    // Fields are read through the accessor of a record.
    protected override string MemberName(string name, string owner) => options.Records ? FieldMember(name) + "()" : FieldMember(name);

    private static string EnumMember(string name) => EnumReserved.Contains(name) ? name + "_" : Id(name);

    /// <summary>The package of a definition: the target's, or for a library's definition a package of its own inside it.</summary>
    private string PackageOf(TypeDef definition) => definition.Owner == null
        ? options.Package
        : DependencyOf(definition)?.Package ?? $"{options.Package}.{Id(definition.Owner.Name.ToLowerInvariant())}";

    /// <summary>The name of a definition in the generated code: the project's own share the package, a library's are qualified.</summary>
    /// <summary>
    /// The name of a definition in the generated code: the project's own definitions go unqualified, except one named
    /// <c>Case</c>, which a variant's nested enum <c>Case</c> would hide.
    /// </summary>
    private string TypeName(TypeDef definition) => definition.Owner == null && JavaName(definition) != "Case"
        ? JavaName(definition)
        : $"{PackageOf(definition)}.{JavaName(definition)}";

    /// <summary>The Java class name of a definition (see <see cref="JavaNames"/>).</summary>
    private string JavaName(TypeDef definition) => JavaNames.TypeName(Project, definition, Definition);

    private static string Capitalize(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>Flags enums and enums with unknown values are kept as <c>long</c> numbers, the others are Java enums.</summary>
    private static bool IsOpen(EnumDef enumDef) => enumDef.Flags || enumDef.AllowUnknown;

    private EnumDef? ClosedEnum(TypeRef type) =>
        Unalias(type) is NamedRef named && Definition(named.Name) is EnumDef { } enumDef && !IsOpen(enumDef) ? enumDef : null;

    private bool Arrays => options.Collections == JavaCollections.Array;

    /// <summary>Unsigned values use the signed type of the same size.</summary>
    private bool SameSize => options.Unsigned != JavaUnsigned.Widen;

    private static bool IsUnsigned(PrimitiveKind kind) => kind is PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 or PrimitiveKind.VarUInt32
        or PrimitiveKind.U64 or PrimitiveKind.VarUInt64;

    /// <summary>The Java type of a primitive, following the <c>unsigned</c> option.</summary>
    private string PrimitiveType(PrimitiveKind kind) => SameSize
        ? kind switch
        {
            PrimitiveKind.U8 => "byte",
            PrimitiveKind.U16 => "short",
            PrimitiveKind.U32 or PrimitiveKind.VarUInt32 => "int",
            _ => RuntimeType(kind),
        }
        : RuntimeType(kind);

    /// <summary>The type the runtime reads and writes a primitive as (unsigned values widened).</summary>
    private static string RuntimeType(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => "boolean",
        PrimitiveKind.U8 or PrimitiveKind.U16 => "int",
        PrimitiveKind.I8 => "byte",
        PrimitiveKind.I16 => "short",
        PrimitiveKind.I32 or PrimitiveKind.VarInt32 or PrimitiveKind.ZigZag32 => "int",
        PrimitiveKind.U32 or PrimitiveKind.VarUInt32 => "long",
        PrimitiveKind.U64 or PrimitiveKind.VarUInt64 or PrimitiveKind.I64 or PrimitiveKind.VarInt64 or PrimitiveKind.ZigZag64 => "long",
        PrimitiveKind.F16 or PrimitiveKind.F32 => "float",
        PrimitiveKind.F64 => "double",
        _ => "java.util.UUID",
    };

    /// <summary>The Java type of an enum's values.</summary>
    private string EnumType(EnumDef enumDef) => PrimitiveType(enumDef.Type.Kind);

    /// <summary>The numeric value of a primitive (of <see cref="PrimitiveType"/>) as a long; unsigned values stay positive.</summary>
    private string AsLong(string value, PrimitiveKind kind) => SameSize
        ? kind switch
        {
            PrimitiveKind.U8 => $"Byte.toUnsignedLong({value})",
            PrimitiveKind.U16 => $"Short.toUnsignedLong({value})",
            PrimitiveKind.U32 or PrimitiveKind.VarUInt32 => $"Integer.toUnsignedLong({value})",
            _ => $"(long) {value}",
        }
        : $"(long) {value}";

    /// <summary>A value of <paramref name="kind"/> converted for the runtime's writer of <paramref name="wire"/>.</summary>
    private string WireValue(string value, PrimitiveKind kind, PrimitiveKind wire)
    {
        // The runtime only writes the low bits, so values of the same wire size need no conversion.
        if (kind == wire) return value;
        var type = RuntimeType(wire);
        return type == "long" ? AsLong(value, kind) : $"({type}) ({AsLong(value, kind)})";
    }

    /// <summary>
    /// With <c>unsigned: checked</c>, values of unsigned types must fit the signed type, so enum members and tags above
    /// its range can't be represented.
    /// </summary>
    private void CheckMember(string what, decimal value, PrimitiveKind kind)
    {
        if (options.Unsigned != JavaUnsigned.Checked || !IsUnsigned(kind)) return;
        var max = PrimitiveType(kind) switch { "byte" => sbyte.MaxValue, "short" => short.MaxValue, "int" => int.MaxValue, _ => long.MaxValue };
        if (value > max)
            throw new CodegenException($"{what} = {value} doesn't fit a {PrimitiveType(kind)} (the option unsigned is checked); use widen or signed");
    }

    /// <summary>A literal of a numeric Java type; values of unsigned types above the signed range keep their bits.</summary>
    private static string TypedLiteral(decimal value, string type) => type switch
    {
        "long" => LongLiteral(value),
        "int" when value is >= int.MinValue and <= int.MaxValue => value.ToString(CultureInfo.InvariantCulture),
        _ => $"({type}) {LongLiteral(value)}",
    };

    private static string Box(string type) => type switch
    {
        "boolean" => "Boolean",
        "byte" => "Byte",
        "short" => "Short",
        "int" => "Integer",
        "long" => "Long",
        "float" => "Float",
        "double" => "Double",
        _ => type,
    };

    private string NativeType(NativeRef native)
    {
        var (codegen, args) = Native(native);
        return NativeTemplate(codegen.Type, args, new Dictionary<string, string>());
    }

    private static bool IsPrimitive(string type) => Box(type) != type;

    /// <summary>The Java type of a value; <paramref name="boxed"/> for generic arguments and values that may be null.</summary>
    private string Type(TypeRef type, bool boxed = false)
    {
        var result = type switch
        {
            PrimitiveRef primitive => PrimitiveType(primitive.Kind),
            StringRef => "String",
            BytesRef => "byte[]",
            NativeRef native => NativeType(native),
            ArrayRef array when Arrays => $"{Type(array.Element)}[]",
            ArrayRef array => $"java.util.List<{Type(array.Element, true)}>",
            MapRef map => $"java.util.List<java.util.Map.Entry<{Type(map.Key, true)}, {Type(map.Value, true)}>>",
            OptionalRef optional => Type(optional.Inner, true),
            SizedRef sized => Type(sized.Inner),
            FixedPointRef => "double",
            NamedRef named when BoundType(named) is { } bound => bound,
            NamedRef named => Definition(named.Name) switch
            {
                AliasDef alias when !IsExcluded(named.Name) => Type(alias.Target),
                EnumDef enumDef when IsOpen(enumDef) => EnumType(enumDef),
                var definition => TypeName(definition),
            },
            _ => throw new CodegenException($"Unsupported type {type}"),
        };
        return boxed ? Box(result) : result;
    }

    private string FieldType(FieldDef field) => Type(field.Type, field.If != null);

    private static string DefaultValue(string type) => type switch
    {
        "boolean" => "false",
        _ when IsPrimitive(type) => "0",
        _ => "null",
    };

    private static string Literal(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or (> '~' and < ' ') => $"\\{System.Convert.ToString(c, 8).PadLeft(3, '0')}",
                > '~' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    private static string LongLiteral(decimal value) => value > long.MaxValue
        ? $"0x{(ulong)value:X}L"
        : $"{value.ToString(CultureInfo.InvariantCulture)}L";

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\\", "&#92;").Replace("*/", "*&#47;").Replace("@", "&#64;");

    private static void Doc(CodeWriter w, string? doc, params string?[] remarks) => Doc(w, doc, [], remarks);

    /// <param name="tags">Block tags after the text, e.g. <c>@param</c>, escaped already.</param>
    private static void Doc(CodeWriter w, string? doc, IReadOnlyList<string> tags, params string?[] remarks)
    {
        var lines = doc?.Split('\n').Select(Escape).ToList() ?? [];
        var parts = remarks.Where(r => r != null).ToList();
        if (parts.Count > 0)
        {
            if (lines.Count > 0) lines.Add("<p>");
            lines.Add(Escape(string.Join(" ", parts)));
        }

        if (lines.Count > 0 && tags.Count > 0) lines.Add("");
        lines.AddRange(tags);

        if (lines.Count == 0) return;
        if (lines.Count == 1)
        {
            w.Line($"/** {lines[0]} */");
            return;
        }

        w.Line("/**");
        foreach (var line in lines) w.Line((" * " + line).TrimEnd());
        w.Line(" */");
    }

    private string Header(string body)
    {
        var builder = new StringBuilder();
        builder.Append(JavaGenerator.FileMarker).Append('\n');
        builder.Append($"// Generated by format-studio from project '{Project.Settings.Name}'{(Project.Settings.Version != null ? $" ({Project.Settings.Version})" : "")}.\n");
        builder.Append("// Do not edit: change the format definitions and regenerate.\n");
        var package = Current != null ? PackageOf(Current) : options.Package;
        builder.Append($"package {package};\n\n");
        if (options.RuntimePackage != package)
        {
            var imports = new[] { "FormatDataException", "FormatEncoding", "FormatPacket", "FormatPacketVisitor", "FormatReader", "FormatSerializable", "FormatTextPacket",
                    "FormatTextPacketVisitor", "FormatWriter" }
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(body, $@"\b{name}\b")).ToList();
            foreach (var name in imports) builder.Append($"import {options.RuntimePackage}.{name};\n");
            if (imports.Count > 0) builder.Append('\n');
        }

        return builder.Append(body).ToString();
    }

    private string Throw(string message) => $"throw new FormatDataException({message});";

    // ------------------------------------------------------------------ expression syntax

    protected override string NumberLiteral(decimal value) => LongLiteral(value);

    protected override string StringLiteral(string text) => Literal(text);

    protected override Value EnumConstant(EnumDef enumDef, string member)
    {
        var access = $"{TypeName(enumDef)}.{EnumMember(member)}";
        return new Value(AsLong(IsOpen(enumDef) ? access : $"{access}.value", enumDef.Type.Kind), access, ValueKind.Number, new NamedRef(enumDef.QualifiedName));
    }

    protected override string Convert(string access, ValueKind kind, TypeRef declared, bool mayBeNull)
    {
        if (kind == ValueKind.Bool) return mayBeNull ? $"({access} != null && {access})" : access;
        var converted = kind == ValueKind.Float ? $"(double) {access}"
            : Unalias(declared) switch
            {
                PrimitiveRef primitive => AsLong(access, primitive.Kind),
                NamedRef named when Definition(named.Name) is EnumDef enumDef => AsLong(IsOpen(enumDef) ? access : $"{access}.value", enumDef.Type.Kind),
                _ => $"(long) {access}",
            };
        return mayBeNull ? $"({access} != null ? {converted} : 0{(kind == ValueKind.Float ? "d" : "L")})" : converted;
    }

    protected override string IndexAccess(string target, string index) => Arrays ? $"{target}[{index}]" : $"{target}.get({index})";

    protected override string Length(Value value)
    {
        TypeRef? type = value.Type;
        while (type != null && Unalias(type) is var resolved && resolved is OptionalRef or SizedRef)
            type = resolved is OptionalRef optional ? optional.Inner : ((SizedRef)resolved).Inner;
        var suffix = (type != null ? Unalias(type) : null) switch
        {
            StringRef => ".length()",
            BytesRef => ".length",
            NativeRef native when NativeType(native).EndsWith("[]", StringComparison.Ordinal) => ".length",
            ArrayRef when Arrays => ".length",
            null when value.Kind == ValueKind.Text => ".length()",
            _ => ".size()",
        };
        return value.MayBeNull ? $"({value.Raw} != null ? (long) {value.Raw}{suffix} : 0L)" : $"(long) {value.Raw}{suffix}";
    }

    protected override string ByteLength(Value value) => $"(long) FormatWriter.utf8Length({value.Raw})";

    protected override string TagNumber(Value value)
    {
        var access = $"{value.Raw}.kind.value";
        return value.Type != null && Unalias(value.Type) is NamedRef named && Definition(named.Name) is VariantDef variant
            && KindValueKind(variant) is { } kind
            ? AsLong(access, kind)
            : $"(long) {access}";
    }

    protected override string TagText(VariantDef variant, Value value) => $"{value.Raw}.kind.tag";

    protected override string TextCompare(string left, string right, string op) =>
        $"{(op == "!=" ? "!" : "")}java.util.Objects.equals({left}, {right})";

    // ------------------------------------------------------------------ enums and bitfields

    protected override string Enum(EnumDef enumDef)
    {
        var w = new CodeWriter();
        var remarks = new[] { enumDef.Title != null ? $"Schema name: {enumDef.Title}." : null, $"Default wire type: {enumDef.Type}." };
        var type = EnumType(enumDef);
        foreach (var member in enumDef.Members) CheckMember($"{enumDef.Name}.{member.Name}", member.Value, enumDef.Type.Kind);
        if (IsOpen(enumDef))
        {
            Doc(w, enumDef.Doc, [.. remarks, enumDef.Flags ? $"A flags enum: values are {type} numbers." : $"Allows unknown values: values are {type} numbers."]);
            w.OpenInline($"public final class {enumDef.Name}");
            w.Line($"private {enumDef.Name}() {{");
            w.Line("}");
            foreach (var member in enumDef.Members)
            {
                w.Line();
                Doc(w, member.Doc, member.Title != null ? $"Schema name: {member.Title}." : null);
                w.Line($"public static final {type} {EnumMember(member.Name)} = {TypedLiteral(member.Value, type)};");
            }

            w.Close();
            return Header(w.ToString());
        }

        Doc(w, enumDef.Doc, remarks);
        w.OpenInline($"public enum {enumDef.Name}");
        for (var i = 0; i < enumDef.Members.Count; i++)
        {
            var member = enumDef.Members[i];
            Doc(w, member.Doc, member.Title != null ? $"Schema name: {member.Title}." : null);
            w.Line($"{EnumMember(member.Name)}({TypedLiteral(member.Value, type)}){(i == enumDef.Members.Count - 1 ? ";" : ",")}");
        }

        if (enumDef.Members.Count == 0) w.Line(";");
        w.Line();
        EnumBody(w, enumDef.Name, "value", type, Box(type), $"\"Unknown {enumDef.Name} value \" + {AsLong("value", enumDef.Type.Kind)}");
        w.Close();
        return Header(w.ToString());
    }

    /// <summary>The key field, constructor and lookup of a Java enum (<c>value</c>/<c>fromValue</c> or <c>tag</c>/<c>fromTag</c>).</summary>
    /// <param name="unknown">The Java expression of the message for an unknown key (named like <paramref name="field"/>).</param>
    private void EnumBody(CodeWriter w, string name, string field, string type, string boxed, string unknown)
    {
        var map = field == "value" ? "BY_VALUE" : "BY_TAG";
        var lookup = field == "value" ? "fromValue" : "fromTag";
        w.Line($"private static final java.util.Map<{boxed}, {name}> {map} = new java.util.HashMap<>();");
        w.Line();
        w.OpenInline("static");
        w.Line($"for ({name} member : values()) {map}.putIfAbsent(member.{field}, member);");
        w.Close();
        w.Line();
        w.Line($"/** The {(field == "value" ? "value on the wire" : "tag")}. */");
        w.Line($"public final {type} {field};");
        w.Line();
        w.OpenInline($"{name}({type} {field})");
        w.Line($"this.{field} = {field};");
        w.Close();
        w.Line();
        w.Line($"/** The member with a {field}; throws for unknown ones. */");
        w.OpenInline($"public static {name} {lookup}({type} {field}) throws FormatDataException");
        w.Line($"{name} member = {map}.get({field});");
        w.Line($"if (member == null) {Throw(unknown)}");
        w.Line("return member;");
        w.Close();
    }

    private string BitMemberType(BitFieldMember member) => member.Type switch
    {
        "bool" => "boolean",
        null => member.Signed ? member.Bits <= 32 ? "int" : "long" : member.Bits <= 31 ? "int" : "long",
        var enumName => Definition(enumName) is EnumDef enumDef && IsOpen(enumDef) ? EnumType(enumDef) : enumName,
    };

    protected override string Bitfield(BitfieldDef bitfield)
    {
        var name = bitfield.Name;
        var w = new CodeWriter();
        Doc(w, bitfield.Doc, $"Packed into {bitfield.Base}.");
        w.OpenInline($"public final class {name} implements FormatSerializable");
        foreach (var member in bitfield.Members) w.Line($"public {BitMemberType(member)} {Member(member.Name)};");
        w.Line();

        w.OpenInline($"public static {name} read(FormatReader reader) throws {IOException}");
        w.Line($"long raw = reader.read{Method(bitfield.Base)}();");
        w.Line($"{name} result = new {name}();");
        foreach (var (member, shift) in Simulation.Bitfields.Layout(bitfield))
        {
            var mask = member.Bits >= 64 ? "-1L" : $"0x{(1UL << member.Bits) - 1:X}L";
            var field = Member(member.Name);
            var value = member.Signed
                ? $"(raw << {64 - shift - member.Bits}) >> {64 - member.Bits}"
                : $"(raw >>> {shift}) & {mask}";
            var type = BitMemberType(member);
            w.Line(member.Type switch
            {
                "bool" => $"result.{field} = ((raw >>> {shift}) & 1L) != 0;",
                _ when IsPrimitive(type) => $"result.{field} = ({type}) ({value});",
                var enumName => $"result.{field} = {enumName}.fromValue(({EnumType((EnumDef)Definition(enumName!))}) ({value}));",
            });
        }

        w.Line("return result;");
        w.Close();
        w.Line();

        w.Line("@Override");
        w.OpenInline($"public void write(FormatWriter writer) throws {IOException}");
        w.Line("long raw = 0;");
        foreach (var (member, shift) in Simulation.Bitfields.Layout(bitfield))
        {
            var mask = member.Bits >= 64 ? "-1L" : $"0x{(1UL << member.Bits) - 1:X}L";
            var field = Member(member.Name);
            var value = BitMemberType(member) switch
            {
                "boolean" => $"(this.{field} ? 1L : 0L)",
                var type when IsPrimitive(type) => $"(long) this.{field}",
                _ => $"this.{field}.value",
            };
            w.Line($"raw |= ({value} & {mask}) << {shift};");
        }

        w.Line($"writer.write{Method(bitfield.Base)}(({RuntimeType(bitfield.Base.Kind)}) raw);");
        w.Close();
        w.Close();
        return Header(w.ToString());
    }

    // ------------------------------------------------------------------ structs

    protected override string Struct(StructDef structDef)
    {
        var name = structDef.Name;
        var w = new CodeWriter();
        var interfaceName = !structDef.IsPacket ? "FormatSerializable" : structDef.TextId != null ? "FormatTextPacket" : "FormatPacket";
        var remarks = new[] { structDef.Title != null ? $"Schema name: {structDef.Title}." : null, structDef.IsPacket ? $"Packet id {structDef.IdText}." : null };
        var fields = structDef.Fields.Where(f => f.IsInput).ToList();
        foreach (var field in fields) WarnUnsigned64(field.Type, $"{name}.{field.Name}", structDef);
        if (options.Records)
        {
            // The fields' documentation goes to the record's @param tags.
            Doc(w, structDef.Doc, fields.Select(f => $"@param {FieldMember(f.Name)} {Escape(FieldRemarks(f))}").ToList(), remarks);
            w.OpenInline($"public record {name}({string.Join(", ", fields.Select(f => $"{FieldType(f)} {FieldMember(f.Name)}"))}) implements {interfaceName}");
        }
        else
        {
            Doc(w, structDef.Doc, remarks);
            w.OpenInline($"public final class {name} implements {interfaceName}");
        }

        if (structDef.IsPacket)
        {
            if (structDef.TextId != null) w.Line($"public static final String ID = {Literal(structDef.TextId)};");
            else if (structDef.Id is not { } id) throw new CodegenException("A packet needs an id");
            else if (id is < int.MinValue or > int.MaxValue) throw new CodegenException($"Packet id {id} doesn't fit a Java int");
            else w.Line($"public static final int ID = {id};");
            w.Line();
        }

        foreach (var field in options.Records ? [] : fields)
        {
            Doc(w, field.Doc, field.Title != null ? $"Schema name: {field.Title}." : null, $"Wire: {Describe(field.Type)}.",
                field.If != null ? $"Only present if {field.If}." : null);
            w.Line($"public {FieldType(field)} {FieldMember(field.Name)};");
            w.Line();
        }

        if (structDef.IsPacket)
        {
            w.Line("@Override");
            w.OpenInline($"public {(structDef.TextId != null ? "String" : "int")} packetId()");
            w.Line("return ID;");
            w.Close();
            w.Line();
        }

        ReadMethod(structDef, w);
        w.Line();
        WriteMethod(structDef, w);
        w.Close();
        return Header(w.ToString());
    }

    /// <summary>The documentation of a record component: its doc and remarks on one line.</summary>
    private string FieldRemarks(FieldDef field) => string.Join(" ", new[]
    {
        field.Doc?.ReplaceLineEndings(" "), field.Title != null ? $"Schema name: {field.Title}." : null, $"Wire: {Describe(field.Type)}.",
        field.If != null ? $"Only present if {field.If}." : null,
    }.Where(r => r != null));

    private string Access(FieldDef field, string prefix) => field.IsInput ? prefix + FieldMember(field.Name) : $"__{field.Name}";

    private string NotEqual(FieldDef field, string left, string right) =>
        Unalias(field.Type) is StringRef ? $"!java.util.Objects.equals({left}, {right})" : $"{left} != {right}";

    private void ReadMethod(StructDef structDef, CodeWriter w)
    {
        var name = structDef.Name;
        w.OpenInline($"public static {name} read(FormatReader reader) throws {IOException}");
        if (options.Records)
        {
            // A record is created from all of its components at once: they are read into locals first.
            var fields = structDef.Fields.Where(f => f.IsInput).ToList();
            foreach (var field in fields)
            {
                var type = FieldType(field);
                w.Line($"{type} {Access(field, "__")} = {DefaultValue(type)};");
            }

            ReadFields(structDef, w, f => Access(f, "__"));
            w.Line($"return new {name}({string.Join(", ", fields.Select(f => Access(f, "__")))});");
            w.Close();
            return;
        }

        w.Line($"{name} result = new {name}();");
        ReadFields(structDef, w, f => Access(f, "result."));
        w.Line("return result;");
        w.Close();
    }

    protected override void DeclareLocal(FieldDef field, string name, CodeWriter w)
    {
        var type = FieldType(field);
        w.Line($"{type} {name} = {DefaultValue(type)};");
    }

    protected override void OpenIf(string condition, CodeWriter w) => w.OpenInline($"if ({condition})");

    protected override void CheckConstant(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        var constant = Constant(field);
        w.Line($"if ({NotEqual(field, value, constant)}) {Throw(Literal($"{structDef.Name}.{field.Name} must be {constant}"))}");
    }

    protected override void CheckRange(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        if (!options.ValidateRanges || Unalias(field.Type) is not PrimitiveRef { Kind: not PrimitiveKind.Uuid and not PrimitiveKind.Bool } primitive) return;
        var kind = primitive.Kind;
        var checks = new List<string>();
        if (kind is PrimitiveKind.U64 or PrimitiveKind.VarUInt64)
        {
            if (field.Min is { } min) checks.Add($"Long.compareUnsigned({value}, {LongLiteral(min)}) < 0");
            if (field.Max is { } max) checks.Add($"Long.compareUnsigned({value}, {LongLiteral(max)}) > 0");
        }
        else
        {
            var number = kind is PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 ? value : AsLong(value, kind);
            if (field.Min is { } min) checks.Add($"{number} < {min.ToString(CultureInfo.InvariantCulture)}");
            if (field.Max is { } max) checks.Add($"{number} > {max.ToString(CultureInfo.InvariantCulture)}");
        }

        w.Line($"if ({string.Join(" || ", checks)}) {Throw(Literal($"{structDef.Name}.{field.Name} is out of range"))}");
    }

    protected override string Constant(FieldDef field)
    {
        var resolved = Unalias(field.Type);
        var constant = field.Const!;
        switch (resolved)
        {
            case NamedRef named when Definition(named.Name) is EnumDef enumDef:
                var memberName = constant.GetValueKind() == JsonValueKind.String ? constant.GetValue<string>() : null;
                if (memberName != null && enumDef.FindByName(memberName) != null) return $"{TypeName(enumDef)}.{EnumMember(memberName)}";
                var number = Simulation.JsonValues.Number(constant) ?? throw new CodegenException($"Invalid const {constant}");
                if (IsOpen(enumDef)) return TypedLiteral(number, EnumType(enumDef));
                var byValue = enumDef.FindByValue((long)number) ?? throw new CodegenException($"{number} is not a member of {enumDef.Name}");
                return $"{TypeName(enumDef)}.{EnumMember(byValue.Name)}";
            case PrimitiveRef { Kind: PrimitiveKind.Bool }:
                return constant.GetValueKind() == JsonValueKind.True ? "true" : "false";
            case PrimitiveRef { Kind: PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 } primitive:
                return $"({PrimitiveType(primitive.Kind)}) {Simulation.JsonValues.Double(constant)?.ToString("R", CultureInfo.InvariantCulture)}d";
            case PrimitiveRef { Kind: not PrimitiveKind.Uuid } primitive:
                return $"({PrimitiveType(primitive.Kind)}) {LongLiteral(Simulation.JsonValues.Number(constant) ?? 0)}";
            case StringRef:
                return Literal(constant.ToString());
            default:
                throw new CodegenException($"A const of type {Describe(field.Type)} is not supported");
        }
    }

    private void WriteMethod(StructDef structDef, CodeWriter w)
    {
        w.Line("@Override");
        w.OpenInline($"public void write(FormatWriter writer) throws {IOException}");
        WriteFields(structDef, w, f => Access(f, "this."));
        w.Close();
    }

    protected override void DeclareComputed(FieldDef field, string name, string code, ValueKind kind, CodeWriter w)
    {
        var type = Type(field.Type);
        if (type == "boolean") code = kind == ValueKind.Bool ? code : $"({code} != 0)";
        else if (kind == ValueKind.Bool) code = $"({type}) ({code} ? 1 : 0)";
        else if (ClosedEnum(field.Type) is { } enumDef) code = $"{TypeName(enumDef)}.fromValue(({EnumType(enumDef)}) ({code}))";
        else code = $"({type}) ({code})";
        w.Line($"{type} {name} = {code};");
    }

    protected override string RequirePresent(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        var present = Temp("value");
        w.Line($"{FieldType(field)} {present} = {value};");
        w.Line($"if ({present} == null) {Throw(Literal($"{structDef.Name}.{field.Name} is required when {field.If}"))}");
        return present;
    }

    // ------------------------------------------------------------------ reading values

    protected override string ReadLengthPrefix(PrimitiveRef prefix, Context context) => $"{context.Reader}.read{Method(prefix)}()";

    protected override string CheckCount(string raw, int minSize, int? max, CodeWriter w, Context context)
    {
        var count = Temp("count");
        w.Line($"int {count} = {context.Reader}.checkCount({raw}, {minSize}{(max != null ? $", {max}" : "")});");
        return count;
    }

    /// <summary>
    /// Converts <paramref name="raw"/>, as the runtime reads <paramref name="wire"/>, to the Java type of
    /// <paramref name="kind"/>. With <c>unsigned: checked</c>, unsigned values that don't fit are rejected.
    /// </summary>
    private string Narrow(string raw, PrimitiveKind wire, PrimitiveKind kind, CodeWriter w, Context context)
    {
        var type = PrimitiveType(kind);
        var value = RuntimeType(wire) == type ? raw : $"({type}) {raw}";
        if (options.Unsigned != JavaUnsigned.Checked || !IsUnsigned(kind)) return value;
        var temp = Temp("value");
        w.Line($"{type} {temp} = {value};");
        w.Line($"if ({temp} < 0) {Throw(Literal($"{context.Field?.Name ?? "A value"} doesn't fit a {type}"))}");
        return temp;
    }

    /// <summary>With <c>unsigned: checked</c>, rejects negative values of unsigned types before writing them.</summary>
    private string CheckFits(string value, PrimitiveKind kind, CodeWriter w, Context context)
    {
        if (options.Unsigned != JavaUnsigned.Checked || !IsUnsigned(kind)) return value;
        var temp = Temp("value");
        var type = PrimitiveType(kind);
        w.Line($"{type} {temp} = {value};");
        w.Line($"if ({temp} < 0) {Throw(Literal($"{context.Field?.Name ?? "A value"} must not be negative (it is unsigned)"))}");
        return temp;
    }

    /// <summary>The expression of a new array with <paramref name="count"/> elements of <paramref name="elementType"/>.</summary>
    private static string NewArray(string elementType, string count)
    {
        if (elementType.Contains('<'))
            throw new CodegenException($"Java can't create arrays of {elementType}; use the option collections: list");
        var nested = elementType.IndexOf('[');
        return nested < 0 ? $"new {elementType}[{count}]" : $"new {elementType[..nested]}[{count}]{elementType[nested..]}";
    }

    protected override string ReadPrimitive(PrimitiveRef primitive, CodeWriter w, Context context) =>
        Narrow($"{context.Reader}.read{Method(primitive)}()", primitive.Kind, primitive.Kind, w, context);

    protected override string ReadString(StringRef str, CodeWriter w, Context context)
    {
        var read = ReadText(str, w, context);
        if (str.Max is not { } max) return read;
        var text = Temp("text");
        w.Line($"String {text} = {read};");
        w.Line($"if ({text}.length() > {max}) {Throw($"{Literal($"{What(context)} has ")} + {text}.length() + {Literal($" characters, more than {max}")}")}");
        return text;
    }

    private string ReadText(StringRef str, CodeWriter w, Context context)
    {
        var reader = context.Reader;
        var encoding = EncodingArgument(str);
        var strict = options.ValidateStrings ? "true" : "false";
        var count = str.Length.Kind is LengthKind.Zero or LengthKind.Eof ? null : ReadCount(str.Length, w, context, 1, StringByteLimit(str));
        if (str.Padding == StringPadding.Terminated)
        {
            var check = options.ValidateStrings && StrictPadding(str) ? "true" : "false";
            return encoding == null
                ? $"{reader}.readUtf8Terminated({count}, {strict}, {check})"
                : $"{reader}.readStringTerminated({count}, {encoding}, {strict}, {check})";
        }

        if (str.Padding == StringPadding.Padded)
        {
            return encoding == null
                ? $"{reader}.readUtf8Padded({count}, {PadByte(str)}, {strict})"
                : $"{reader}.readStringPadded({count}, {PadByte(str)}, {encoding}, {strict})";
        }

        var with = encoding == null ? "" : encoding + ", ";
        var name = encoding == null ? "Utf8" : "String";
        return str.Length.Kind switch
        {
            LengthKind.Zero => $"{reader}.read{name}Zero({with}{strict})",
            LengthKind.Eof => $"{reader}.read{name}ToEnd({with}{strict})",
            _ => $"{reader}.read{name}({count}, {with}{strict})",
        };
    }

    /// <summary>The runtime's <c>FormatEncoding</c> of a string, or null for UTF-8 (which has methods of its own).</summary>
    private string? EncodingArgument(StringRef str) => (str.Encoding ?? Project.Defaults.String) switch
    {
        StringEncoding.Utf8 => null,
        StringEncoding.Ascii => "FormatEncoding.ASCII",
        StringEncoding.Latin1 => "FormatEncoding.LATIN1",
        var other => throw new CodegenException($"String encoding {Dsl.TypeExpressions.EncodingName(other)} is not supported by the Java generator"),
    };

    /// <summary>The number of bytes a string takes in its encoding.</summary>
    private string ByteCount(StringRef str, string value) => EncodingArgument(str) is { } encoding
        ? $"FormatWriter.byteLength({value}, {encoding})"
        : $"FormatWriter.utf8Length({value})";

    protected override string ReadBytes(BytesRef bytes, CodeWriter w, Context context)
    {
        if (bytes.Length.Kind == LengthKind.Eof) return $"{context.Reader}.readToEnd()";
        return $"{context.Reader}.readBytes({ReadCount(bytes.Length, w, context, 1, context.Field?.MaxItems)})";
    }

    protected override string ReadArray(ArrayRef array, CodeWriter w, Context context)
    {
        var elementType = Type(array.Element, !Arrays);
        var boxed = Box(elementType);
        var result = Temp("array");
        var elementContext = context.Element();
        if (array.Length.Kind is LengthKind.Until or LengthKind.Eof)
        {
            var list = Arrays ? Temp("list") : result;
            w.Line($"java.util.List<{boxed}> {list} = new java.util.ArrayList<>();");
            if (array.Length.Kind == LengthKind.Until)
            {
                var item = Temp("item");
                w.OpenInline("while (true)");
                w.Line($"{elementType} {item} = {Read(array.Element, w, elementContext)};");
                w.Line($"{list}.add({item});");
                w.Line($"if ({Condition(array.Length.Expression!, ElementContext(array, item))}) break;");
            }
            else
            {
                w.OpenInline($"while (!{context.Reader}.atEnd())");
                w.Line($"{list}.add({Read(array.Element, w, elementContext)});");
            }

            w.Close();
            if (!Arrays) return result;
            w.Line($"{elementType}[] {result} = {NewArray(elementType, $"{list}.size()")};");
            var copy = Temp("i");
            w.Line($"for (int {copy} = 0; {copy} < {result}.length; {copy}++) {result}[{copy}] = {list}.get({copy});");
            return result;
        }

        var count = ReadCount(array.Length, w, context, MinSize(array.Element), context.Field?.MaxItems);
        w.Line(Arrays
            ? $"{elementType}[] {result} = {NewArray(elementType, count)};"
            : $"java.util.List<{elementType}> {result} = new java.util.ArrayList<>(Math.min({count}, 4096));");
        var index = Temp("i");
        w.OpenInline($"for (int {index} = 0; {index} < {count}; {index}++)");
        var element = Read(array.Element, w, elementContext);
        w.Line(Arrays ? $"{result}[{index}] = {element};" : $"{result}.add({element});");
        w.Close();
        return result;
    }

    protected override string ReadMap(MapRef map, CodeWriter w, Context context)
    {
        var keyType = Type(map.Key, true);
        var valueType = Type(map.Value, true);
        var count = ReadCount(map.Length, w, context, MinSize(map.Key), context.Field?.MaxItems);
        var result = Temp("map");
        w.Line($"java.util.List<java.util.Map.Entry<{keyType}, {valueType}>> {result} = new java.util.ArrayList<>(Math.min({count}, 4096));");
        var index = Temp("i");
        var entryContext = context.Element();
        w.OpenInline($"for (int {index} = 0; {index} < {count}; {index}++)");
        var key = Temp("key");
        var value = Temp("value");
        w.Line($"{keyType} {key} = {Read(map.Key, w, entryContext)};");
        w.Line($"{valueType} {value} = {Read(map.Value, w, entryContext)};");
        w.Line($"{result}.add(new java.util.AbstractMap.SimpleEntry<>({key}, {value}));");
        w.Close();
        return result;
    }

    protected override string ReadOptional(OptionalRef optional, CodeWriter w, Context context)
    {
        var result = Temp("optional");
        w.Line($"{Type(optional.Inner, true)} {result} = null;");
        w.OpenInline($"if ({context.Reader}.readBool())");
        w.Line($"{result} = {Read(optional.Inner, w, context.Element())};");
        w.Close();
        return result;
    }

    protected override string TypeArgument(TypeRef type) => Type(type);

    /// <summary>A local <c>FormatReader.ValueReader&lt;T&gt;</c> lambda.</summary>
    protected override string ReadFunction(TypeRef type, CodeWriter w, Context context)
    {
        var name = Temp("read");
        var reader = Temp("reader");
        w.OpenInline($"FormatReader.ValueReader<{Type(type, true)}> {name} = {reader} ->");
        var value = Read(type, w, context.WithReader(reader));
        w.Line($"return {value};");
        w.Close(";");
        return name;
    }

    /// <summary>A local <c>FormatWriter.ValueWriter&lt;T&gt;</c> lambda.</summary>
    protected override string WriteFunction(TypeRef type, CodeWriter w, Context context)
    {
        var name = Temp("write");
        var writer = Temp("writer");
        var boxed = Temp("value");
        w.OpenInline($"FormatWriter.ValueWriter<{Type(type, true)}> {name} = ({writer}, {boxed}) ->");
        var value = boxed;
        if (IsPrimitive(Type(type)))
        {
            // Unboxed, so that the writing code can cast it.
            value = Temp("value");
            w.Line($"{Type(type)} {value} = {boxed};");
        }

        Write(type, value, w, context.WithWriter(writer));
        w.Close(";");
        return name;
    }

    protected override string ReadSized(SizedRef sized, CodeWriter w, Context context)
    {
        var count = ReadCount(sized.Length, w, context, 1, null);
        var sub = Temp("reader");
        w.Line($"FormatReader {sub} = {context.Reader}.slice({count});");
        if (sized.Fill == null) return Read(sized.Inner, w, context.WithReader(sub));
        var value = Temp("value");
        w.Line($"{Type(sized.Inner)} {value} = {Read(sized.Inner, w, context.WithReader(sub))};");
        w.Line($"{sub}.skipFill(new byte[] {{ {JavaFillBytes(sized)} }}, {(options.ValidateStrings && StrictFill(sized) ? "true" : "false")});");
        return value;
    }

    protected override string ReadFixedPoint(FixedPointRef fixedPoint, CodeWriter w, Context context) =>
        $"({context.Reader}.read{Method(fixedPoint.Base)}() / {Math.Pow(2, fixedPoint.FractionBits).ToString("R", CultureInfo.InvariantCulture)}d)";

    protected override string ReadEnum(NamedRef named, EnumDef enumDef, CodeWriter w, Context context)
    {
        var wire = named.As ?? enumDef.Type;
        var raw = $"{context.Reader}.read{Method(wire)}()";
        if (IsOpen(enumDef)) return Narrow(raw, wire.Kind, enumDef.Type.Kind, w, context);
        var enumType = EnumType(enumDef);
        return $"{TypeName(enumDef)}.fromValue({(RuntimeType(wire.Kind) == enumType ? raw : $"({enumType}) {raw}")})";
    }

    protected override string ReadTaggedVariant(NamedRef named, VariantDef variant, CodeWriter w, Context context)
    {
        if (Project.TagKind(variant) == VariantTagKind.String)
            return $"{TypeName(variant)}.read({context.Reader}, {TypeName(variant)}.Case.fromTag({Expression(named.Tag!, context, out _)}))";
        var tag = Expression(named.Tag!, context, out _);
        var kindValueType = KindValueType(variant);
        return $"{TypeName(variant)}.read({context.Reader}, {VariantKindType(variant)}.fromValue({(kindValueType == "long" ? tag : $"({kindValueType}) ({tag})")}))";
    }

    protected override string ReadDefinition(TypeDef definition, CodeWriter w, Context context) => $"{TypeName(definition)}.read({context.Reader})";

    // ------------------------------------------------------------------ writing values

    protected override void WriteLengthPrefix(PrimitiveRef prefix, string count, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.write{Method(prefix)}(({RuntimeType(prefix.Kind)}) {count});");

    protected override void CheckFixedCount(string count, int length, string what, CodeWriter w, Context context) =>
        w.Line($"if ({count} != {length}) {Throw(Literal($"{what} must have exactly {length} elements"))}");

    private void WritePadded(StringRef str, string value, CodeWriter w, Context context)
    {
        var encoding = EncodingArgument(str);
        var text = Temp("text");
        var size = Temp("size");
        w.Line($"String {text} = {value};");
        w.Line($"int {size} = (int) ({PaddedSize(str.Length, context)});");
        w.Line($"if ({ByteCount(str, text)} > {size}) {Throw($"{Literal($"{What(context)} must take at most ")} + {size} + \" bytes\"")}");
        if (str.Padding == StringPadding.Terminated)
            w.Line($"if ({text}.indexOf('\\0') >= 0) {Throw(Literal($"{What(context)} must not contain NUL"))}");
        if (str.Padding == StringPadding.Padded && options.ValidateStrings && StrictPadding(str))
            w.Line($"if ({text}.endsWith(String.valueOf((char) {PadByte(str)}))) {Throw(Literal($"{What(context)} must not end with its pad character"))}");
        w.Line(encoding == null
            ? $"{context.Writer}.writeUtf8Padded({text}, {size}, {PadByte(str)});"
            : $"{context.Writer}.writeStringPadded({text}, {size}, {PadByte(str)}, {encoding});");
    }

    /// <summary>The fill pattern as Java byte literals (bytes are signed, so each is cast).</summary>
    private static string JavaFillBytes(SizedRef sized) => string.Join(", ", FillBytes(sized).Split(", ").Select(b => "(byte) " + b));

    private static string What(Context context) => context.Field?.Name ?? "value";

    protected override void WritePrimitive(PrimitiveRef primitive, string value, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.write{Method(primitive)}({CheckFits(value, primitive.Kind, w, context)});");

    protected override void WriteString(StringRef str, string value, CodeWriter w, Context context)
    {
        var encoding = EncodingArgument(str);
        if (str.Max is { } max) w.Line($"if ({value}.length() > {max}) {Throw(Literal($"{What(context)} has more than {max} characters"))}");
        if (str.Padding != StringPadding.None)
        {
            WritePadded(str, value, w, context);
            return;
        }

        var with = encoding == null ? "" : ", " + encoding;
        var name = encoding == null ? "Utf8" : "String";
        if (str.Length.Kind == LengthKind.Zero)
        {
            w.Line($"{context.Writer}.write{name}Zero({value}{with});");
            return;
        }

        if (str.Length.Kind is LengthKind.Default or LengthKind.Prefix or LengthKind.Fixed)
            WriteCount(str.Length, ByteCount(str, value), w, context, What(context));
        w.Line($"{context.Writer}.write{name}({value}{with});");
    }

    protected override void WriteBytes(BytesRef bytes, string value, CodeWriter w, Context context)
    {
        WriteCount(bytes.Length, $"{value}.length", w, context, What(context));
        w.Line($"{context.Writer}.writeBytes({value});");
    }

    protected override void WriteArray(ArrayRef array, string value, CodeWriter w, Context context)
    {
        var items = Temp("items");
        w.Line($"{Type(array)} {items} = {value};");
        WriteCount(array.Length, Arrays ? $"{items}.length" : $"{items}.size()", w, context, What(context));
        var item = Temp("item");
        w.OpenInline($"for ({Type(array.Element, !Arrays)} {item} : {items})");
        Write(array.Element, item, w, context.Element());
        w.Close();
    }

    protected override void WriteMap(MapRef map, string value, CodeWriter w, Context context)
    {
        var entries = Temp("entries");
        w.Line($"{Type(map)} {entries} = {value};");
        WriteCount(map.Length, $"{entries}.size()", w, context, What(context));
        var entry = Temp("entry");
        var entryContext = context.Element();
        w.OpenInline($"for (java.util.Map.Entry<{Type(map.Key, true)}, {Type(map.Value, true)}> {entry} : {entries})");
        Write(map.Key, $"{entry}.getKey()", w, entryContext);
        Write(map.Value, $"{entry}.getValue()", w, entryContext);
        w.Close();
    }

    protected override void WriteOptional(OptionalRef optional, string value, CodeWriter w, Context context)
    {
        var temp = Temp("optional");
        w.Line($"{Type(optional.Inner, true)} {temp} = {value};");
        w.Line($"{context.Writer}.writeBool({temp} != null);");
        w.OpenInline($"if ({temp} != null)");
        Write(optional.Inner, temp, w, context.Element());
        w.Close();
    }

    protected override void WriteSized(SizedRef sized, string value, CodeWriter w, Context context)
    {
        var buffer = Temp("buffer");
        var sub = Temp("writer");
        w.Line($"java.io.ByteArrayOutputStream {buffer} = new java.io.ByteArrayOutputStream();");
        w.Line($"FormatWriter {sub} = new FormatWriter({buffer});");
        Write(sized.Inner, value, w, context.WithWriter(sub));
        if (sized.Fill == null)
        {
            WriteCount(sized.Length, $"{buffer}.size()", w, context, What(context));
            w.Line($"{context.Writer}.writeBytes({buffer}.toByteArray());");
            return;
        }

        var size = Temp("size");
        w.Line($"int {size} = (int) ({PaddedSize(sized.Length, context)});");
        w.Line($"if ({buffer}.size() > {size}) {Throw($"{Literal($"{What(context)} must take at most ")} + {size} + \" bytes\"")}");
        w.Line($"{context.Writer}.writeBytes({buffer}.toByteArray());");
        w.Line($"{context.Writer}.writeFill(new byte[] {{ {JavaFillBytes(sized)} }}, {size} - {buffer}.size());");
    }

    protected override void WriteFixedPoint(FixedPointRef fixedPoint, string value, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.write{Method(fixedPoint.Base)}(({RuntimeType(fixedPoint.Base.Kind)}) Math.round({value} * " +
               $"{Math.Pow(2, fixedPoint.FractionBits).ToString("R", CultureInfo.InvariantCulture)}d));");

    protected override void WriteEnum(NamedRef named, EnumDef enumDef, string value, CodeWriter w, Context context)
    {
        var wire = named.As ?? enumDef.Type;
        var number = IsOpen(enumDef) ? CheckFits(value, enumDef.Type.Kind, w, context) : value + ".value";
        w.Line($"{context.Writer}.write{Method(wire)}({WireValue(number, enumDef.Type.Kind, wire.Kind)});");
    }

    protected override void WriteTaggedVariant(NamedRef named, VariantDef variant, string value, CodeWriter w, Context context) =>
        w.Line($"{value}.writeContent({context.Writer});");

    protected override void WriteDefinition(TypeDef definition, string value, CodeWriter w, Context context) =>
        w.Line($"{value}.write({context.Writer});");

    // ------------------------------------------------------------------ variants

    /// <summary>
    /// Whether <c>kind</c> is the tag enum itself; otherwise the variant has a nested <c>Case</c> enum (integer and
    /// string tags, and tag enums kept as numbers).
    /// </summary>
    private EnumDef? KindEnum(VariantDef variant) => TagEnum(variant) is { } enumDef && !IsOpen(enumDef) ? enumDef : null;

    private string VariantKindType(VariantDef variant) => KindEnum(variant) is { } kindEnum ? TypeName(kindEnum) : $"{TypeName(variant)}.Case";

    /// <summary>The primitive kind of <c>kind.value</c>; null when it is a long (a <c>Case</c> enum of an external tag).</summary>
    private PrimitiveKind? KindValueKind(VariantDef variant) => KindEnum(variant)?.Type.Kind ?? TagWire(variant)?.Kind;

    /// <summary>The Java type of <c>kind.value</c> of a variant with numeric tags.</summary>
    private string KindValueType(VariantDef variant) => KindValueKind(variant) is { } kind ? PrimitiveType(kind) : "long";

    /// <summary>The enum constant of a case in <c>switch</c> labels.</summary>
    private string CaseLabel(VariantDef variant, VariantCase variantCase)
    {
        if (KindEnum(variant) is not { } enumDef) return EnumMember(variantCase.Name);
        var member = variantCase.TagMember != null ? enumDef.FindByName(variantCase.TagMember) : enumDef.FindByValue(variantCase.TagValue ?? 0);
        return EnumMember(member?.Name ?? throw new CodegenException($"Case {variantCase.Name}: the tag is not a member of {enumDef.Name}"));
    }

    /// <summary>The numeric tag of a case of a variant with a nested <c>Case</c> enum.</summary>
    private long CaseTag(VariantDef variant, VariantCase variantCase)
    {
        if (variantCase.TagValue is { } value) return value;
        if (TagEnum(variant)?.FindByName(variantCase.TagMember ?? "") is { } member) return member.Value;
        throw new CodegenException($"Case {variantCase.Name} has no tag value");
    }

    protected override string Variant(VariantDef variant)
    {
        var name = JavaName(variant);
        if (name != variant.Name)
            Output.Diagnostics.Warning($"{variant.Name}: named {name} in Java, since its cases are a nested enum Case");
        var w = new CodeWriter();
        var stringTags = Project.TagKind(variant) == VariantTagKind.String;
        var kindType = KindEnum(variant) is { } kindEnum ? TypeName(kindEnum) : "Case";
        Doc(w, variant.Doc, variant.Tag != null ? $"Tag: {Describe(variant.Tag)}." : "The tag is stored outside (see the fields using it).");
        w.OpenInline($"public final class {name} implements FormatSerializable");

        if (kindType == "Case")
        {
            w.Line($"/** The cases{(stringTags ? " and their tags" : " and their tag values")}. */");
            w.OpenInline("public enum Case");
            for (var i = 0; i < variant.Cases.Count; i++)
            {
                var variantCase = variant.Cases[i];
                if (!stringTags && KindValueKind(variant) is { } valueKind) CheckMember($"{name}.{variantCase.Name}", CaseTag(variant, variantCase), valueKind);
                var key = stringTags ? Literal(variantCase.TagMember!) : TypedLiteral(CaseTag(variant, variantCase), KindValueType(variant));
                w.Line($"{EnumMember(variantCase.Name)}({key}){(i == variant.Cases.Count - 1 ? ";" : ",")}");
            }

            if (variant.Cases.Count == 0) w.Line(";");
            w.Line();
            if (stringTags) EnumBody(w, "Case", "tag", "String", "String", $"\"Unknown {name} tag '\" + tag + \"'\"");
            else EnumBody(w, "Case", "value", KindValueType(variant), Box(KindValueType(variant)), $"\"Unknown {name} tag \" + value");
            w.Close();
            w.Line();
        }

        w.Line("/** The active case. */");
        w.Line($"public {kindType} kind;");
        w.Line();
        foreach (var variantCase in variant.Cases.Where(c => c.Type != null))
        {
            WarnUnsigned64(variantCase.Type!, $"{name}.{variantCase.Name}", variant);
            Doc(w, variantCase.Doc ?? $"The value of case {variantCase.Name}; only meaningful when kind is {variantCase.Name}.",
                $"Wire: {Describe(variantCase.Type!)}.");
            w.Line($"public {Type(variantCase.Type!)} {Member(variantCase.Name)};");
            w.Line();
        }

        foreach (var variantCase in variant.Cases)
        {
            var parameter = variantCase.Type != null ? $"{Type(variantCase.Type)} value" : "";
            w.OpenInline($"public static {name} of{Capitalize(variantCase.Name)}({parameter})");
            w.Line($"{name} result = new {name}();");
            w.Line($"result.kind = {kindType}.{CaseLabel(variant, variantCase)};");
            if (variantCase.Type != null) w.Line($"result.{Member(variantCase.Name)} = value;");
            w.Line("return result;");
            w.Close();
            w.Line();
        }

        var tagWire = TagWire(variant);
        w.OpenInline($"public static {name} read(FormatReader reader) throws {IOException}");
        if (variant.Tag != null && stringTags)
            w.Line($"return read(reader, Case.fromTag({Read(variant.Tag, w, Context.Empty)}));");
        else if (tagWire != null)
        {
            // No range check: fromValue rejects everything that is not a case.
            var kindValueType = KindValueType(variant);
            var raw = $"reader.read{Method(tagWire)}()";
            w.Line($"return read(reader, {kindType}.fromValue({(RuntimeType(tagWire.Kind) == kindValueType ? raw : $"({kindValueType}) {raw}")}));");
        }
        else
            w.Line(Throw(Literal($"{name} needs its tag from the enclosing type")));
        w.Close();
        w.Line();

        w.OpenInline($"public static {name} read(FormatReader reader, {kindType} kind) throws {IOException}");
        w.Line($"{name} result = new {name}();");
        w.Line("result.kind = kind;");
        w.OpenInline("switch (kind)");
        foreach (var variantCase in variant.Cases)
        {
            w.Line($"case {CaseLabel(variant, variantCase)}:");
            w.Indent();
            if (variantCase.Type != null) w.Line($"result.{Member(variantCase.Name)} = {Read(variantCase.Type, w, Context.Empty)};");
            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Line($"    {Throw($"\"Unknown {name} tag \" + kind")}");
        w.Close();
        w.Line("return result;");
        w.Close();
        w.Line();

        w.Line("@Override");
        w.OpenInline($"public void write(FormatWriter writer) throws {IOException}");
        if (variant.Tag != null && stringTags) Write(variant.Tag, "this.kind.tag", w, Context.Empty);
        else if (tagWire != null) w.Line($"writer.write{Method(tagWire)}({WireValue("this.kind.value", KindValueKind(variant)!.Value, tagWire.Kind)});");
        w.Line("writeContent(writer);");
        w.Close();
        w.Line();

        w.Line("/** Writes the value of the active case without the tag. */");
        w.OpenInline($"public void writeContent(FormatWriter writer) throws {IOException}");
        w.OpenInline("switch (this.kind)");
        foreach (var variantCase in variant.Cases)
        {
            w.Line($"case {CaseLabel(variant, variantCase)}:");
            w.Indent();
            if (variantCase.Type != null) Write(variantCase.Type, $"this.{Member(variantCase.Name)}", w, Context.Empty);
            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Line($"    {Throw($"\"Unknown {name} tag \" + this.kind")}");
        w.Close();
        w.Close();
        w.Close();
        return Header(w.ToString());
    }

    /// <summary>With <c>unsigned: widen</c>, warns that 64-bit unsigned values (which have no larger Java type) keep their bits in a long.</summary>
    private void WarnUnsigned64(TypeRef type, string what, TypeDef owner)
    {
        if (options.Unsigned == JavaUnsigned.Widen && HasUnsigned64(type))
            Output.Diagnostics.Warning($"{what}: Java has no type wider than long, so this 64-bit unsigned value keeps its bits in a long " +
                                       "(values above Long.MAX_VALUE are negative); set the option unsigned to signed or checked to accept this", owner.Source);
    }

    private bool HasUnsigned64(TypeRef type) => type switch
    {
        PrimitiveRef primitive => primitive.Kind is PrimitiveKind.U64 or PrimitiveKind.VarUInt64,
        ArrayRef array => HasUnsigned64(array.Element),
        MapRef map => HasUnsigned64(map.Key) || HasUnsigned64(map.Value),
        OptionalRef optional => HasUnsigned64(optional.Inner),
        SizedRef sized => HasUnsigned64(sized.Inner),
        NamedRef named => Definition(named.Name) switch
        {
            AliasDef alias when !IsExcluded(named.Name) => HasUnsigned64(alias.Target),
            EnumDef enumDef => IsOpen(enumDef) && enumDef.Type.Kind is PrimitiveKind.U64 or PrimitiveKind.VarUInt64,
            _ => false,
        },
        _ => false,
    };

    // ------------------------------------------------------------------ registry

    /// <summary>
    /// The registry class; with protocols it holds a nested class per protocol (<c>Packets.LoginServerbound.read</c>).
    /// </summary>
    private string Registry()
    {
        var w = new CodeWriter();
        var registries = Registries(options.Registry)
            .Select(r => (r.Protocol, Packets: r.Packets.Where(p => p.TextId != null || p.Id is >= int.MinValue and <= int.MaxValue).ToList()))
            .ToList();
        if (registries is [(null, var all)])
        {
            w.Line($"/** Packet id dispatch for the {all.Count} packets of '{Escape(Project.Settings.Name)}'. */");
            w.OpenInline($"public final class {options.Registry}");
            RegistryMembers(w, options.Registry, all);
            w.Close();
            return Header(w.ToString());
        }

        w.Line($"/** Packet id dispatch for the {registries.Count} protocols of '{Escape(Project.Settings.Name)}'. */");
        w.OpenInline($"public final class {options.Registry}");
        w.Line($"private {options.Registry}() {{");
        w.Line("}");
        foreach (var (protocol, packets) in registries)
        {
            w.Line();
            Doc(w, protocol!.Doc ?? $"Packet id dispatch for protocol {protocol.Name}.", ProtocolRemark(protocol, packets.Count));
            w.OpenInline($"public static final class {protocol.Name}");
            RegistryMembers(w, protocol.Name, packets);
            w.Close();
        }

        w.Close();
        return Header(w.ToString());
    }

    /// <param name="className">The class the members are in, for its private constructor.</param>
    private void RegistryMembers(CodeWriter w, string className, List<StructDef> packets)
    {
        // A registry of text ids switches over strings (Java 7+) and has the text packet interfaces.
        var text = IsTextRegistry(packets);
        var (idType, packetType, visitorType) = text ? ("String", "FormatTextPacket", "FormatTextPacketVisitor") : ("int", "FormatPacket", "FormatPacketVisitor");
        string Key(StructDef packet) => text ? Literal(packet.TextId!) : packet.Id!.Value.ToString(CultureInfo.InvariantCulture);

        w.Line($"private static final {idType}[] IDS = {{{string.Join(", ", packets.Select(Key))}}};");
        w.Line();
        w.Line($"private {className}() {{");
        w.Line("}");
        w.Line();
        w.Line("/** Reads the packet with the given id, or returns null for unknown ids. */");
        w.OpenInline($"public static {packetType} read({idType} id, FormatReader reader) throws {IOException}");
        w.OpenInline("switch (id)");
        foreach (var packet in packets)
        {
            w.Line($"case {Key(packet)}:");
            w.Line($"    return {TypeName(packet)}.read(reader);");
        }

        w.Line("default:");
        w.Line("    return null;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("/** Reads the packet with the given id and passes it to the visitor. Returns false for unknown ids. */");
        w.OpenInline($"public static boolean dispatch({idType} id, FormatReader reader, {visitorType} visitor) throws {IOException}");
        w.Line($"{packetType} packet = read(id, reader);");
        w.Line("if (packet == null) return false;");
        w.Line("visitor.visit(packet);");
        w.Line("return true;");
        w.Close();
        w.Line();
        w.Line("/** The packet name for an id, or null. */");
        w.OpenInline($"public static String nameOf({idType} id)");
        w.OpenInline("switch (id)");
        foreach (var packet in packets)
        {
            w.Line($"case {Key(packet)}:");
            w.Line($"    return {Literal(packet.Name)};");
        }

        w.Line("default:");
        w.Line("    return null;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("/** All packet ids. */");
        w.OpenInline($"public static {idType}[] ids()");
        w.Line("return IDS.clone();");
        w.Close();
    }
}
