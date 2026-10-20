using System.Globalization;
using System.Text;
using System.Text.Json;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen.CSharp;

/// <summary>C# syntax for the <see cref="CodeEmitter"/>; spans and streams differ only in a few places (see <see cref="CSharpIo"/>).</summary>
internal sealed class CSharpEmitter(FormatProject project, CSharpOptions options) : CodeEmitter(project, options.Exclude)
{
    private HashSet<string>? _classes;

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    /// <summary>Member names the generated code itself uses.</summary>
    private static readonly HashSet<string> Reserved = ["Read", "Write", "WriteContent", "Id", "PacketId", "Kind", "Case"];

    private bool Streams => options.Io == CSharpIo.Stream;

    protected override string PathOf(TypeDef definition)
    {
        var folder = definition switch
        {
            StructDef { IsPacket: true } => "Packets",
            StructDef or BitfieldDef => "Types",
            EnumDef => "Enums",
            _ => "Variants",
        };
        var name = CsName(definition);
        return definition.Owner == null ? $"{folder}/{name}.g.cs" : $"{Pascal(definition.Owner.Name)}/{folder}/{name}.g.cs";
    }

    protected override string Generator => Streams ? "csharp-stream" : "csharp";

    protected override string Marker => CSharpGenerator.FileMarker;

    protected override string NativeNamespace => options.Namespace + ".Natives";

    protected override string RuntimeNamespace => options.RuntimeNamespace;

    protected override string NativeFilePath(string fileName) => $"Natives/{fileName}";

    protected override void Finish()
    {
        if (Project.Packets.Any()) Output.Files.Add(new GeneratedFile($"{options.Registry}.g.cs", Registry()));
    }

    // ------------------------------------------------------------------ I/O model

    private string ReaderParameter => Streams ? $"{options.ReaderType} reader" : $"ref {options.ReaderType} reader";

    private string WriterParameter(string name = "writer") => Streams ? $"{options.WriterType} {name}" : $"ref {options.WriterType} {name}";

    /// <summary>A reader or writer as an argument (by reference for spans).</summary>
    private string Pass(string name) => Streams ? name : "ref " + name;

    // ------------------------------------------------------------------ names and types

    private static string Id(string name) => Keywords.Contains(name) ? "@" + name : name;

    /// <summary>The namespace of a definition: the target's, or for a library's definition a namespace of its own inside it.</summary>
    private string NamespaceOf(TypeDef definition) => definition.Owner == null
        ? options.Namespace
        : DependencyOf(definition)?.Namespace ?? $"{options.Namespace}.{Pascal(definition.Owner.Name)}";

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private string Global(TypeDef definition) => $"global::{NamespaceOf(definition)}.{CsName(definition)}";

    /// <param name="reference">A type name as written in the definitions, e.g. <c>Slot</c> or <c>mc.Slot</c>.</param>
    private string Global(string reference) => Global(Definition(reference));

    /// <summary>The name of a definition in the generated code; the project's own definitions may go unqualified.</summary>
    private string TypeName(TypeDef definition, bool qualified) => qualified || definition.Owner != null ? Global(definition) : CsName(definition);

    /// <summary>The C# name of a definition (see <see cref="CSharpNames"/>).</summary>
    private string CsName(TypeDef definition) => CSharpNames.TypeName(Project, definition, Definition);

    /// <summary>Warns when a definition's C# name isn't its own.</summary>
    private void WarnRenamed(TypeDef definition)
    {
        if (CsName(definition) is var name && name != definition.Name)
            Output.Diagnostics.Warning($"{definition.Name}: named {name} in C#, since {definition.Name} is a member the generator gives the type");
    }

    /// <summary>The C# member name of a field or case, avoiding the enclosing type name and generated members.</summary>
    private static string Member(string name, string owner) => name == owner || Reserved.Contains(name) ? name + "Value" : Id(name);

    protected override string MemberName(string name, string owner) =>
        Member(name, Generated.FirstOrDefault(d => d.Name == owner) is { } definition ? CsName(definition) : owner);

    /// <summary>Types that contain themselves by value must be classes.</summary>
    private bool IsClass(TypeDef definition) => (_classes ??= SelfContainingTypes()).Contains(definition.QualifiedName);

    private static string PrimitiveType(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => "bool",
        PrimitiveKind.U8 => "byte",
        PrimitiveKind.I8 => "sbyte",
        PrimitiveKind.U16 => "ushort",
        PrimitiveKind.I16 => "short",
        PrimitiveKind.U32 or PrimitiveKind.VarUInt32 => "uint",
        PrimitiveKind.I32 or PrimitiveKind.VarInt32 or PrimitiveKind.ZigZag32 => "int",
        PrimitiveKind.U64 or PrimitiveKind.VarUInt64 => "ulong",
        PrimitiveKind.I64 or PrimitiveKind.VarInt64 or PrimitiveKind.ZigZag64 => "long",
        PrimitiveKind.F16 => "global::System.Half",
        PrimitiveKind.F32 => "float",
        PrimitiveKind.F64 => "double",
        _ => "global::System.Guid",
    };

    /// <summary>The C# type of a value; <paramref name="qualified"/> uses global:: names (for method bodies).</summary>
    private string Type(TypeRef type, bool qualified = false) => type switch
    {
        PrimitiveRef primitive => PrimitiveType(primitive.Kind),
        StringRef => "string",
        BytesRef => "byte[]",
        NativeRef native => NativeType(native),
        ArrayRef array => Type(array.Element, qualified) + "[]",
        MapRef map => $"global::System.Collections.Generic.KeyValuePair<{Type(map.Key, qualified)}, {Type(map.Value, qualified)}>[]",
        OptionalRef optional => Nullable(Type(optional.Inner, qualified)),
        SizedRef sized => Type(sized.Inner, qualified),
        FixedPointRef => "double",
        NamedRef named => Definition(named.Name) is AliasDef alias && !IsExcluded(named.Name)
            ? Type(alias.Target, qualified)
            : TypeName(Definition(named.Name), qualified),
        _ => throw new CodegenException($"Unsupported type {type}"),
    };

    private static string Nullable(string type) => type.EndsWith('?') ? type : type + "?";

    private string NativeType(NativeRef native)
    {
        var (codegen, args) = Native(native);
        return NativeTemplate(codegen.Type, args, new Dictionary<string, string>());
    }

    private bool IsValueType(TypeRef type) => Project.Unalias(type) switch
    {
        PrimitiveRef or FixedPointRef => true,
        SizedRef sized => IsValueType(sized.Inner),
        NativeRef native => ValueTypeArgument(native) is { } inner ? IsValueType(inner) : Native(native).Codegen.IsValueType,
        NamedRef named => Definition(named.Name) is EnumDef or BitfieldDef || Definition(named.Name) is StructDef or VariantDef && !IsClass(Definition(named.Name)),
        _ => false,
    };

    private static string Literal(string text) => JsonSerializer.Serialize(text);

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void Doc(CodeWriter w, string? doc, params string?[] remarks)
    {
        if (doc != null)
        {
            w.Line("/// <summary>");
            foreach (var line in doc.Split('\n')) w.Line(("/// " + Escape(line)).TrimEnd());
            w.Line("/// </summary>");
        }

        var parts = remarks.Where(r => r != null).ToList();
        if (parts.Count > 0) w.Line($"/// <remarks>{Escape(string.Join(" ", parts))}</remarks>");
    }

    private string Header()
    {
        var builder = new StringBuilder();
        builder.Append(CSharpGenerator.FileMarker).Append('\n');
        builder.Append($"// Generated by format-studio from project '{Project.Settings.Name}'{(Project.Settings.Version != null ? $" ({Project.Settings.Version})" : "")}.\n");
        builder.Append("// Do not edit: change the format definitions and regenerate.\n");
        builder.Append("#nullable enable\n");
        builder.Append("#pragma warning disable CS1591, CS0108, CS8618\n\n");
        builder.Append($"using {options.RuntimeNamespace};\n\n");
        builder.Append($"namespace {(Current != null ? NamespaceOf(Current) : options.Namespace)};\n\n");
        return builder.ToString();
    }

    // ------------------------------------------------------------------ expression syntax

    protected override string NumberLiteral(decimal value) =>
        value > long.MaxValue ? $"{value.ToString(CultureInfo.InvariantCulture)}UL" : $"{value.ToString(CultureInfo.InvariantCulture)}L";

    protected override string StringLiteral(string text) => Literal(text);

    protected override Value EnumConstant(EnumDef enumDef, string member)
    {
        var access = $"{Global(enumDef)}.{Id(member)}";
        return new Value($"(long){access}", access, ValueKind.Number, new NamedRef(enumDef.QualifiedName));
    }

    protected override string Convert(string access, ValueKind kind, TypeRef declared, bool mayBeNull)
    {
        var safe = mayBeNull && IsValueType(declared) ? $"({access} ?? default)" : access;
        return kind switch
        {
            ValueKind.Bool => safe,
            ValueKind.Float => $"(double){safe}",
            _ => $"(long){safe}",
        };
    }

    protected override string IndexAccess(string target, string index) => $"{target}[{index}]";

    protected override string Length(Value value) =>
        value.MayBeNull ? $"(long)({value.Raw}?.Length ?? 0)" : $"(long){value.Raw}.Length";

    protected override string ByteLength(Value value) =>
        $"(long)global::System.Text.Encoding.UTF8.GetByteCount({(value.MayBeNull ? $"{value.Raw} ?? \"\"" : value.Raw)})";

    protected override string TagNumber(Value value) => $"(long){value.Raw}.Kind";

    protected override string TagText(VariantDef variant, Value value) => $"{Global(variant)}.TagOf({value.Raw}.Kind)";

    protected override string TextCompare(string left, string right, string op) => $"({left} {op} {right})";

    // ------------------------------------------------------------------ enums and bitfields

    protected override string Enum(EnumDef enumDef)
    {
        var w = new CodeWriter();
        Doc(w, enumDef.Doc, enumDef.Title != null ? $"Schema name: {enumDef.Title}." : null, $"Default wire type: {enumDef.Type}.");
        if (enumDef.Flags) w.Line("[global::System.Flags]");
        w.Open($"public enum {enumDef.Name} : {PrimitiveType(enumDef.Type.Kind)}");
        foreach (var member in enumDef.Members)
        {
            Doc(w, member.Doc, member.Title != null ? $"Schema name: {member.Title}." : null);
            w.Line($"{Id(member.Name)} = {member.Value.ToString(CultureInfo.InvariantCulture)},");
        }

        w.Close();
        return Header() + w;
    }

    protected override string Bitfield(BitfieldDef bitfield)
    {
        WarnRenamed(bitfield);
        var name = CsName(bitfield);
        var w = new CodeWriter();
        Doc(w, bitfield.Doc, $"Packed into {bitfield.Base}.");
        w.Open($"public partial struct {name} : {options.SerializableInterface}<{name}>");
        foreach (var member in bitfield.Members) w.Line($"public {BitMemberType(member)} {Member(member.Name, name)};");
        w.Line();

        w.Open($"public static {name} Read({ReaderParameter})");
        w.Line($"var raw = (ulong)reader.Read{Method(bitfield.Base)}();");
        w.Line($"var result = new {name}();");
        foreach (var (member, shift) in Simulation.Bitfields.Layout(bitfield))
        {
            var mask = member.Bits >= 64 ? "ulong.MaxValue" : $"0x{(1UL << member.Bits) - 1:X}UL";
            var field = Member(member.Name, name);
            var value = member.Signed
                ? $"(long)(raw << {64 - shift - member.Bits}) >> {64 - member.Bits}"
                : $"(raw >> {shift}) & {mask}";
            w.Line(member.Type switch
            {
                "bool" => $"result.{field} = ((raw >> {shift}) & 1UL) != 0;",
                null => $"result.{field} = ({BitMemberType(member)})({value});",
                var enumName => $"result.{field} = ({Global(enumName)})({value});",
            });
        }

        w.Line("return result;");
        w.Close();
        w.Line();

        w.Open($"public readonly void Write({WriterParameter()})");
        w.Line("ulong raw = 0;");
        foreach (var (member, shift) in Simulation.Bitfields.Layout(bitfield))
        {
            var mask = member.Bits >= 64 ? "ulong.MaxValue" : $"0x{(1UL << member.Bits) - 1:X}UL";
            var field = Member(member.Name, name);
            var value = member.Type switch
            {
                "bool" => $"({field} ? 1UL : 0UL)",
                null when member.Signed => $"(ulong)(long){field}",
                null => $"(ulong){field}",
                _ => $"(ulong)(long){field}",
            };
            w.Line($"raw |= ({value} & {mask}) << {shift};");
        }

        w.Line($"writer.Write{Method(bitfield.Base)}(({PrimitiveType(bitfield.Base.Kind)})raw);");
        w.Close();
        w.Close();
        return Header() + w;
    }

    private static string BitMemberType(BitFieldMember member) => member.Type switch
    {
        "bool" => "bool",
        null => (member.Signed, member.Bits) switch
        {
            (true, <= 32) => "int",
            (true, _) => "long",
            (false, <= 32) => "uint",
            _ => "ulong",
        },
        var enumName => enumName,
    };

    // ------------------------------------------------------------------ structs

    protected override string Struct(StructDef structDef)
    {
        WarnRenamed(structDef);
        var name = CsName(structDef);
        var isClass = IsClass(structDef);
        var w = new CodeWriter();
        var remarks = new List<string?>
        {
            structDef.Title != null ? $"Schema name: {structDef.Title}." : null,
            structDef.IsPacket ? $"Packet id {structDef.Id}." : null,
            isClass ? "A class because it contains itself." : null,
        };
        Doc(w, structDef.Doc, remarks.ToArray());

        var interfaceName = structDef.IsPacket ? options.PacketInterface : options.SerializableInterface;
        w.Open($"public {(isClass ? "sealed partial class" : "partial struct")} {name} : {interfaceName}<{name}>");
        if (structDef.IsPacket)
        {
            if (structDef.Id == null) throw new CodegenException("A packet needs an id");
            w.Line($"public const uint Id = {structDef.Id};");
            w.Line();
            w.Line("public static uint PacketId => Id;");
            w.Line();
        }

        foreach (var field in structDef.Fields.Where(f => f.IsInput))
        {
            Doc(w, field.Doc, field.Title != null ? $"Schema name: {field.Title}." : null, $"Wire: {Describe(field.Type)}.",
                field.If != null ? $"Only present if {field.If}." : null);
            w.Line($"public {FieldType(field)} {Member(field.Name, name)};");
            w.Line();
        }

        ReadMethod(structDef, w);
        w.Line();
        WriteMethod(structDef, w, isClass);
        w.Close();
        return Header() + w;
    }

    private string FieldType(FieldDef field)
    {
        var type = Type(field.Type);
        return field.If != null ? Nullable(type) : type;
    }

    private string Access(FieldDef field, string prefix, StructDef owner) =>
        field.IsInput ? prefix + Member(field.Name, CsName(owner)) : $"__{field.Name}";

    private void ReadMethod(StructDef structDef, CodeWriter w)
    {
        w.Open($"public static {CsName(structDef)} Read({ReaderParameter})");
        w.Line($"var result = new {Global(structDef)}();");
        ReadFields(structDef, w, f => Access(f, "result.", structDef));
        w.Line("return result;");
        w.Close();
    }

    protected override void DeclareLocal(FieldDef field, string name, CodeWriter w) =>
        w.Line($"{FieldType(field)} {name} = {(field.If != null || IsValueType(field.Type) ? "default" : "default!")};");

    protected override void OpenIf(string condition, CodeWriter w) => w.Open($"if ({condition})");

    protected override void CheckConstant(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        var constant = Constant(field);
        w.Line($"if ({value} != {constant}) throw new {options.ExceptionType}(\"{structDef.Name}.{field.Name} must be {constant.Replace("\"", "\\\"")}\");");
    }

    protected override void CheckRange(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        if (!options.ValidateRanges || Project.Unalias(field.Type) is not PrimitiveRef { Kind: not PrimitiveKind.Uuid and not PrimitiveKind.Bool } primitive) return;
        // Half only compares with Half.
        if (primitive.Kind == PrimitiveKind.F16) value = $"(double){value}";
        var checks = new List<string>();
        if (field.Min is { } min) checks.Add($"{value} < {min.ToString(CultureInfo.InvariantCulture)}");
        if (field.Max is { } max) checks.Add($"{value} > {max.ToString(CultureInfo.InvariantCulture)}");
        w.Line($"if ({string.Join(" || ", checks)}) throw new {options.ExceptionType}(\"{structDef.Name}.{field.Name} is out of range\");");
    }

    protected override string Constant(FieldDef field)
    {
        var resolved = Project.Unalias(field.Type);
        var constant = field.Const!;
        switch (resolved)
        {
            case NamedRef named when Definition(named.Name) is EnumDef enumDef:
                var memberName = constant.GetValueKind() == JsonValueKind.String ? constant.GetValue<string>() : null;
                if (memberName != null && enumDef.FindByName(memberName) != null) return $"{Global(enumDef)}.{Id(memberName)}";
                return $"({Global(enumDef)}){Simulation.JsonValues.Number(constant)}";
            case PrimitiveRef { Kind: PrimitiveKind.Bool }:
                return constant.GetValueKind() == JsonValueKind.True ? "true" : "false";
            case PrimitiveRef { Kind: PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 } primitive:
                return $"({PrimitiveType(primitive.Kind)}){Simulation.JsonValues.Double(constant)?.ToString("R", CultureInfo.InvariantCulture)}";
            case PrimitiveRef { Kind: not PrimitiveKind.Uuid } primitive:
                return $"({PrimitiveType(primitive.Kind)}){Simulation.JsonValues.Number(constant)?.ToString(CultureInfo.InvariantCulture)}";
            case StringRef:
                return Literal(constant.ToString());
            default:
                throw new CodegenException($"A const of type {Describe(field.Type)} is not supported");
        }
    }

    private void WriteMethod(StructDef structDef, CodeWriter w, bool isClass)
    {
        w.Open($"public {(isClass ? "" : "readonly ")}void Write({WriterParameter()})");
        WriteFields(structDef, w, f => Access(f, "this.", structDef));
        w.Close();
    }

    protected override void DeclareComputed(FieldDef field, string name, string code, ValueKind kind, CodeWriter w)
    {
        var type = Type(field.Type);
        var resolved = Project.Unalias(field.Type);
        if (resolved is PrimitiveRef { Kind: PrimitiveKind.Bool }) code = kind == ValueKind.Bool ? code : $"({code} != 0)";
        else if (kind == ValueKind.Bool) code = $"({type})({code} ? 1 : 0)";
        else code = resolved is NamedRef ? $"({Type(field.Type, true)})({code})" : $"({type})({code})";
        w.Line($"{type} {name} = {code};");
    }

    protected override string RequirePresent(StructDef structDef, FieldDef field, string value, CodeWriter w)
    {
        var present = Temp("value");
        w.Line($"var {present} = {value} ?? throw new {options.ExceptionType}(\"{structDef.Name}.{field.Name} is required when {field.If!.Replace("\"", "'")}\");");
        return present;
    }

    // ------------------------------------------------------------------ reading values

    protected override string PassReader(string reader) => Pass(reader);

    protected override string TypeArgument(TypeRef type) => Type(type, true);

    /// <summary>A static local function <c>T f(ref FormatReader reader)</c> (without <c>ref</c> for streams).</summary>
    protected override string ReadFunction(TypeRef type, CodeWriter w, Context context)
    {
        var name = Temp("read");
        var reader = Temp("reader");
        w.Open($"static {Type(type, true)} {name}({(Streams ? "" : "ref ")}{options.ReaderType} {reader})");
        var value = Read(type, w, context.WithReader(reader));
        w.Line($"return {value};");
        w.Close();
        return name;
    }

    /// <summary>A static local function <c>void f(ref FormatWriter writer, T value)</c> (without <c>ref</c> for streams).</summary>
    protected override string WriteFunction(TypeRef type, CodeWriter w, Context context)
    {
        var name = Temp("write");
        var writer = Temp("writer");
        var value = Temp("value");
        w.Open($"static void {name}({WriterParameter(writer)}, {Type(type, true)} {value})");
        Write(type, value, w, context.WithWriter(writer));
        w.Close();
        return name;
    }

    protected override string ReadLengthPrefix(PrimitiveRef prefix, Context context) => $"{context.Reader}.Read{Method(prefix)}()";

    protected override string CheckCount(string raw, int minSize, int? max, CodeWriter w, Context context)
    {
        var count = Temp("count");
        w.Line($"var {count} = {context.Reader}.CheckCount({raw}, {minSize}{(max != null ? $", {max}" : "")});");
        return count;
    }

    protected override string ReadPrimitive(PrimitiveRef primitive, CodeWriter w, Context context) => $"{context.Reader}.Read{Method(primitive)}()";

    protected override string ReadString(StringRef str, CodeWriter w, Context context)
    {
        var read = ReadText(str, w, context);
        if (str.Max is not { } max) return read;
        var text = Temp("text");
        w.Line($"var {text} = {read};");
        w.Line($"if ({text}.Length > {max}) throw new {options.ExceptionType}($\"{What(context)} has {{{text}.Length}} characters, more than {max}\");");
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
                ? $"{reader}.ReadUtf8Terminated({count}, {strict}, {check})"
                : $"{reader}.ReadStringTerminated({count}, {encoding}, {strict}, {check})";
        }

        if (str.Padding == StringPadding.Padded)
        {
            return encoding == null
                ? $"{reader}.ReadUtf8Padded({count}, {PadByte(str)}, {strict})"
                : $"{reader}.ReadStringPadded({count}, {PadByte(str)}, {encoding}, {strict})";
        }

        var with = encoding == null ? "" : encoding + ", ";
        var name = encoding == null ? "Utf8" : "String";
        return str.Length.Kind switch
        {
            LengthKind.Zero => $"{reader}.Read{name}Zero({with}{strict})",
            LengthKind.Eof when Streams => $"{reader}.Read{name}ToEnd({with}{strict})",
            LengthKind.Eof => $"{reader}.Read{name}({reader}.Remaining, {with}{strict})",
            _ => $"{reader}.Read{name}({count}, {with}{strict})",
        };
    }

    /// <summary>The runtime's <c>FormatEncoding</c> of a string, or null for UTF-8 (which has methods of its own).</summary>
    private string? EncodingArgument(StringRef str) => (str.Encoding ?? Project.Defaults.String) switch
    {
        StringEncoding.Utf8 => null,
        StringEncoding.Ascii => "FormatEncoding.Ascii",
        StringEncoding.Latin1 => "FormatEncoding.Latin1",
        var other => throw new CodegenException($"String encoding {Dsl.TypeExpressions.EncodingName(other)} is not supported by the C# generator"),
    };

    /// <summary>The number of bytes a string takes in its encoding.</summary>
    private string ByteCount(StringRef str, string value) => EncodingArgument(str) is { } encoding
        ? $"{options.WriterType}.ByteLength({value}, {encoding})"
        : $"{options.WriterType}.Utf8Length({value})";

    protected override string ReadBytes(BytesRef bytes, CodeWriter w, Context context)
    {
        var reader = context.Reader;
        if (bytes.Length.Kind == LengthKind.Eof) return Streams ? $"{reader}.ReadToEnd()" : $"{reader}.ReadBytes({reader}.Remaining)";
        return $"{reader}.ReadBytes({ReadCount(bytes.Length, w, context, 1, context.Field?.MaxItems)})";
    }

    protected override string ReadArray(ArrayRef array, CodeWriter w, Context context)
    {
        var elementType = Type(array.Element, true);
        var result = Temp("array");
        var elementContext = context.Element();
        if (array.Length.Kind == LengthKind.Until)
        {
            var list = Temp("list");
            var item = Temp("item");
            w.Line($"var {list} = new global::System.Collections.Generic.List<{elementType}>();");
            w.Open("while (true)");
            w.Line($"var {item} = {Read(array.Element, w, elementContext)};");
            w.Line($"{list}.Add({item});");
            w.Line($"if ({Condition(array.Length.Expression!, ElementContext(array, item))}) break;");
            w.Close();
            w.Line($"var {result} = {list}.ToArray();");
            return result;
        }

        if (array.Length.Kind == LengthKind.Eof)
        {
            var list = Temp("list");
            w.Line($"var {list} = new global::System.Collections.Generic.List<{elementType}>();");
            w.Open(Streams ? $"while (!{context.Reader}.AtEnd)" : $"while ({context.Reader}.Remaining > 0)");
            w.Line($"{list}.Add({Read(array.Element, w, elementContext)});");
            w.Close();
            w.Line($"var {result} = {list}.ToArray();");
            return result;
        }

        var count = ReadCount(array.Length, w, context, MinSize(array.Element), context.Field?.MaxItems);
        w.Line($"var {result} = {NewArray(elementType, count)};");
        var index = Temp("i");
        w.Open($"for (var {index} = 0; {index} < {result}.Length; {index}++)");
        w.Line($"{result}[{index}] = {Read(array.Element, w, elementContext)};");
        w.Close();
        return result;
    }

    protected override string ReadMap(MapRef map, CodeWriter w, Context context)
    {
        var entryType = $"global::System.Collections.Generic.KeyValuePair<{Type(map.Key, true)}, {Type(map.Value, true)}>";
        var count = ReadCount(map.Length, w, context, MinSize(map.Key), context.Field?.MaxItems);
        var result = Temp("map");
        w.Line($"var {result} = new {entryType}[{count}];");
        var index = Temp("i");
        var entryContext = context.Element();
        w.Open($"for (var {index} = 0; {index} < {result}.Length; {index}++)");
        var key = Temp("key");
        w.Line($"var {key} = {Read(map.Key, w, entryContext)};");
        w.Line($"{result}[{index}] = new {entryType}({key}, {Read(map.Value, w, entryContext)});");
        w.Close();
        return result;
    }

    protected override string ReadOptional(OptionalRef optional, CodeWriter w, Context context)
    {
        var result = Temp("optional");
        w.Line($"{Nullable(Type(optional.Inner, true))} {result} = null;");
        w.Open($"if ({context.Reader}.ReadBool())");
        w.Line($"{result} = {Read(optional.Inner, w, context.Element())};");
        w.Close();
        return result;
    }

    protected override string ReadSized(SizedRef sized, CodeWriter w, Context context)
    {
        var count = ReadCount(sized.Length, w, context, 1, null);
        var sub = Temp("reader");
        w.Line($"var {sub} = {context.Reader}.Slice({count});");
        if (sized.Fill == null) return Read(sized.Inner, w, context.WithReader(sub));
        var value = Temp("value");
        w.Line($"var {value} = {Read(sized.Inner, w, context.WithReader(sub))};");
        w.Line($"{sub}.SkipFill(new byte[] {{ {FillBytes(sized)} }}, {(options.ValidateStrings && StrictFill(sized) ? "true" : "false")});");
        return value;
    }

    protected override string ReadFixedPoint(FixedPointRef fixedPoint, CodeWriter w, Context context) =>
        $"({context.Reader}.Read{Method(fixedPoint.Base)}() / {Math.Pow(2, fixedPoint.FractionBits).ToString("R", CultureInfo.InvariantCulture)}d)";

    protected override string ReadEnum(NamedRef named, EnumDef enumDef, CodeWriter w, Context context)
    {
        var enumValue = $"({Global(enumDef)}){context.Reader}.Read{Method(named.As ?? enumDef.Type)}()";
        if (!options.ValidateEnums || enumDef.AllowUnknown || enumDef.Flags) return enumValue;
        var checkedValue = Temp("enum");
        w.Line($"var {checkedValue} = {enumValue};");
        w.Line($"if (!global::System.Enum.IsDefined({checkedValue})) throw new {options.ExceptionType}($\"Unknown {enumDef.Name} value {{{checkedValue}}}\");");
        return checkedValue;
    }

    protected override string ReadTaggedVariant(NamedRef named, VariantDef variant, CodeWriter w, Context context) =>
        Project.TagKind(variant) == VariantTagKind.String
            ? $"{Global(named.Name)}.Read({Pass(context.Reader)}, {Global(named.Name)}.CaseOf({Expression(named.Tag!, context, out _)}))"
            : $"{Global(named.Name)}.Read({Pass(context.Reader)}, ({VariantTagType(variant, true)}){Expression(named.Tag!, context, out _)})";

    protected override string ReadDefinition(TypeDef definition, CodeWriter w, Context context) =>
        $"{Global(definition)}.Read({Pass(context.Reader)})";

    private static string NewArray(string elementType, string count)
    {
        var suffix = "";
        while (elementType.EndsWith("[]", StringComparison.Ordinal))
        {
            suffix += "[]";
            elementType = elementType[..^2];
        }

        return $"new {elementType}[{count}]{suffix}";
    }

    // ------------------------------------------------------------------ writing values

    protected override string PassWriter(string writer) => Pass(writer);

    protected override void WriteLengthPrefix(PrimitiveRef prefix, string count, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.Write{Method(prefix)}(({PrimitiveType(prefix.Kind)}){count});");

    protected override void CheckFixedCount(string count, int length, string what, CodeWriter w, Context context) =>
        w.Line($"if ({count} != {length}) throw new {options.ExceptionType}(\"{what} must have exactly {length} elements\");");

    protected override void WritePrimitive(PrimitiveRef primitive, string value, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.Write{Method(primitive)}({value});");

    protected override void WriteString(StringRef str, string value, CodeWriter w, Context context)
    {
        var encoding = EncodingArgument(str);
        if (str.Max is { } max)
            w.Line($"if ({value}.Length > {max}) throw new {options.ExceptionType}(\"{What(context)} has more than {max} characters\");");
        if (str.Padding != StringPadding.None)
        {
            WritePadded(str, value, w, context);
            return;
        }

        var with = encoding == null ? "" : ", " + encoding;
        var name = encoding == null ? "Utf8" : "String";
        if (str.Length.Kind == LengthKind.Zero)
        {
            w.Line($"{context.Writer}.Write{name}Zero({value}{with});");
            return;
        }

        if (str.Length.Kind is LengthKind.Default or LengthKind.Prefix or LengthKind.Fixed)
            WriteCount(str.Length, ByteCount(str, value), w, context, What(context));
        w.Line($"{context.Writer}.Write{name}({value}{with});");
    }

    private void WritePadded(StringRef str, string value, CodeWriter w, Context context)
    {
        var encoding = EncodingArgument(str);
        var text = Temp("text");
        var size = Temp("size");
        w.Line($"var {text} = {value};");
        w.Line($"var {size} = (int)({PaddedSize(str.Length, context)});");
        w.Line($"if ({ByteCount(str, text)} > {size}) throw new {options.ExceptionType}($\"{What(context)} must take at most {{{size}}} bytes\");");
        if (str.Padding == StringPadding.Terminated)
            w.Line($"if ({text}.Contains('\\0')) throw new {options.ExceptionType}(\"{What(context)} must not contain NUL\");");
        if (str.Padding == StringPadding.Padded && options.ValidateStrings && StrictPadding(str))
            w.Line($"if ({text}.EndsWith((char){PadByte(str)})) throw new {options.ExceptionType}(\"{What(context)} must not end with its pad character\");");
        w.Line(encoding == null
            ? $"{context.Writer}.WriteUtf8Padded({text}, {size}, {PadByte(str)});"
            : $"{context.Writer}.WriteStringPadded({text}, {size}, {PadByte(str)}, {encoding});");
    }

    /// <summary>
    /// Writes the length of a sized value's content, or, with a fill, checks that the content fits its region and returns
    /// the local holding the region's size.
    /// </summary>
    private string? WriteSizedCount(SizedRef sized, string length, CodeWriter w, Context context)
    {
        if (sized.Fill == null)
        {
            WriteCount(sized.Length, length, w, context, What(context));
            return null;
        }

        var size = Temp("size");
        w.Line($"var {size} = (int)({PaddedSize(sized.Length, context)});");
        w.Line($"if ({length} > {size}) throw new {options.ExceptionType}($\"{What(context)} must take at most {{{size}}} bytes\");");
        return size;
    }

    private static void WriteFill(SizedRef sized, string? size, string length, CodeWriter w, Context context)
    {
        if (size != null) w.Line($"{context.Writer}.WriteFill(new byte[] {{ {FillBytes(sized)} }}, {size} - {length});");
    }

    private static string What(Context context) => context.Field?.Name ?? "value";

    protected override void WriteBytes(BytesRef bytes, string value, CodeWriter w, Context context)
    {
        WriteCount(bytes.Length, $"{value}.Length", w, context, What(context));
        w.Line($"{context.Writer}.WriteBytes({value});");
    }

    protected override void WriteArray(ArrayRef array, string value, CodeWriter w, Context context)
    {
        var items = Temp("items");
        w.Line($"var {items} = {value};");
        WriteCount(array.Length, $"{items}.Length", w, context, What(context));
        var item = Temp("item");
        w.Open($"foreach (var {item} in {items})");
        Write(array.Element, item, w, context.Element());
        w.Close();
    }

    protected override void WriteMap(MapRef map, string value, CodeWriter w, Context context)
    {
        var entries = Temp("entries");
        w.Line($"var {entries} = {value};");
        WriteCount(map.Length, $"{entries}.Length", w, context, What(context));
        var entry = Temp("entry");
        var entryContext = context.Element();
        w.Open($"foreach (var {entry} in {entries})");
        Write(map.Key, $"{entry}.Key", w, entryContext);
        Write(map.Value, $"{entry}.Value", w, entryContext);
        w.Close();
    }

    protected override void WriteOptional(OptionalRef optional, string value, CodeWriter w, Context context)
    {
        var temp = Temp("optional");
        w.Line($"var {temp} = {value};");
        w.Line($"{context.Writer}.WriteBool({temp} != null);");
        w.Open($"if ({temp} != null)");
        Write(optional.Inner, IsValueType(optional.Inner) ? $"{temp}.Value" : temp, w, context.Element());
        w.Close();
    }

    protected override void WriteSized(SizedRef sized, string value, CodeWriter w, Context context)
    {
        var buffer = Temp("buffer");
        var sub = Temp("writer");
        if (Streams)
        {
            w.Line($"var {buffer} = new global::System.IO.MemoryStream();");
            w.Line($"var {sub} = new {options.WriterType}({buffer});");
            Write(sized.Inner, value, w, context.WithWriter(sub));
            var length = $"(int){buffer}.Length";
            var size = WriteSizedCount(sized, length, w, context);
            w.Line($"{context.Writer}.WriteBytes(new global::System.ReadOnlySpan<byte>({buffer}.GetBuffer(), 0, {length}));");
            WriteFill(sized, size, length, w, context);
        }
        else
        {
            w.Line($"var {buffer} = new global::System.Buffers.ArrayBufferWriter<byte>();");
            w.Line($"var {sub} = new {options.WriterType}({buffer});");
            Write(sized.Inner, value, w, context.WithWriter(sub));
            var size = WriteSizedCount(sized, $"{buffer}.WrittenCount", w, context);
            w.Line($"{context.Writer}.WriteBytes({buffer}.WrittenSpan);");
            WriteFill(sized, size, $"{buffer}.WrittenCount", w, context);
        }
    }

    protected override void WriteFixedPoint(FixedPointRef fixedPoint, string value, CodeWriter w, Context context) =>
        w.Line($"{context.Writer}.Write{Method(fixedPoint.Base)}(({PrimitiveType(fixedPoint.Base.Kind)})global::System.Math.Round({value} * " +
               $"{Math.Pow(2, fixedPoint.FractionBits).ToString("R", CultureInfo.InvariantCulture)}d));");

    protected override void WriteEnum(NamedRef named, EnumDef enumDef, string value, CodeWriter w, Context context)
    {
        var wire = named.As ?? enumDef.Type;
        w.Line($"{context.Writer}.Write{Method(wire)}(({PrimitiveType(wire.Kind)}){value});");
    }

    protected override void WriteTaggedVariant(NamedRef named, VariantDef variant, string value, CodeWriter w, Context context) =>
        w.Line($"{value}.WriteContent({Pass(context.Writer)});");

    protected override void WriteDefinition(TypeDef definition, string value, CodeWriter w, Context context) =>
        w.Line($"{value}.Write({Pass(context.Writer)});");

    // ------------------------------------------------------------------ variants

    private string VariantTagType(VariantDef variant, bool qualified) =>
        TagEnum(variant) is { } tagEnum
            ? TypeName(tagEnum, qualified)
            : qualified ? $"{Global(variant)}.Case" : "Case";

    private string CaseValue(VariantDef variant, VariantCase variantCase)
    {
        if (TagEnum(variant) is { } enumDef)
        {
            return variantCase.TagMember != null
                ? $"{Global(enumDef)}.{Id(variantCase.TagMember)}"
                : $"({Global(enumDef)}){variantCase.TagValue}";
        }

        return $"{Global(variant)}.Case.{Id(variantCase.Name)}";
    }

    protected override string Variant(VariantDef variant)
    {
        WarnRenamed(variant);
        var name = CsName(variant);
        var isClass = IsClass(variant);
        var w = new CodeWriter();
        var tagType = VariantTagType(variant, false);
        Doc(w, variant.Doc, variant.Tag != null ? $"Tag: {Describe(variant.Tag)}." : "The tag is stored outside (see the fields using it).",
            isClass ? "A class because it contains itself." : null);
        w.Open($"public {(isClass ? "sealed partial class" : "partial struct")} {name} : {options.SerializableInterface}<{name}>");

        var stringTags = Project.TagKind(variant) == VariantTagKind.String;
        var resolvedTag = variant.Tag != null ? Project.Unalias(variant.Tag) : null;
        if (tagType == "Case")
        {
            // String tags are mapped to the case index; CaseOf and TagOf convert.
            var underlying = stringTags ? "int" : resolvedTag is PrimitiveRef primitive ? PrimitiveType(primitive.Kind) : "long";
            w.Open($"public enum Case : {underlying}");
            for (var i = 0; i < variant.Cases.Count; i++)
                w.Line($"{Id(variant.Cases[i].Name)} = {(stringTags ? i : variant.Cases[i].TagValue)},");
            w.Close();
            w.Line();
        }

        if (stringTags)
        {
            w.Line("/// <summary>The case of a tag.</summary>");
            w.Open("public static Case CaseOf(string tag) => tag switch");
            foreach (var variantCase in variant.Cases) w.Line($"{Literal(variantCase.TagMember!)} => Case.{Id(variantCase.Name)},");
            w.Line($"_ => throw new {options.ExceptionType}($\"Unknown {name} tag '{{tag}}'\"),");
            w.Close(";");
            w.Line();
            w.Line("/// <summary>The tag of a case.</summary>");
            w.Open("public static string TagOf(Case kind) => kind switch");
            foreach (var variantCase in variant.Cases) w.Line($"Case.{Id(variantCase.Name)} => {Literal(variantCase.TagMember!)},");
            w.Line($"_ => throw new {options.ExceptionType}($\"Unknown {name} case {{kind}}\"),");
            w.Close(";");
            w.Line();
        }

        w.Line("/// <summary>The active case.</summary>");
        w.Line($"public {tagType} Kind;");
        w.Line();
        foreach (var variantCase in variant.Cases.Where(c => c.Type != null))
        {
            Doc(w, variantCase.Doc ?? $"The value of case {variantCase.Name}; only meaningful when Kind is {variantCase.Name}.",
                $"Wire: {Describe(variantCase.Type!)}.");
            // Qualified: a type named Case would be the nested enum.
            w.Line($"public {Type(variantCase.Type!, true)} {Member(variantCase.Name, name)};");
            w.Line();
        }

        foreach (var variantCase in variant.Cases)
        {
            var member = Member(variantCase.Name, name);
            w.Line(variantCase.Type == null
                ? $"public static {name} {CSharpNames.Factory(variant, variantCase)}() => new() {{ Kind = {CaseValue(variant, variantCase)} }};"
                : $"public static {name} {CSharpNames.Factory(variant, variantCase)}({Type(variantCase.Type, true)} value) => new() {{ Kind = {CaseValue(variant, variantCase)}, {member} = value }};");
        }

        w.Line();
        w.Open($"public static {name} Read({ReaderParameter})");
        if (variant.Tag is NamedRef tagRef && TagEnum(variant) is { } tagEnum)
            w.Line($"return Read({Pass("reader")}, ({Global(tagEnum)})reader.Read{Method(tagRef.As ?? tagEnum.Type)}());");
        else if (variant.Tag != null && stringTags)
            w.Line($"return Read({Pass("reader")}, CaseOf({Read(variant.Tag, w, Context.Empty)}));");
        else if (resolvedTag is PrimitiveRef primitive)
            w.Line($"return Read({Pass("reader")}, ({Global(variant)}.Case)reader.Read{Method(primitive)}());");
        else
            w.Line($"throw new {options.ExceptionType}(\"{name} needs its tag from the enclosing type\");");

        w.Close();
        w.Line();

        w.Open($"public static {name} Read({ReaderParameter}, {tagType} kind)");
        w.Line($"var result = new {Global(variant)} {{ Kind = kind }};");
        w.Open("switch (kind)");
        foreach (var variantCase in variant.Cases)
        {
            w.Line($"case {CaseValue(variant, variantCase)}:");
            w.Indent();
            if (variantCase.Type != null)
                w.Line($"result.{Member(variantCase.Name, name)} = {Read(variantCase.Type, w, Context.Empty)};");
            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Line($"    throw new {options.ExceptionType}($\"Unknown {name} tag {{kind}}\");");
        w.Close();
        w.Line("return result;");
        w.Close();
        w.Line();

        var readOnly = isClass ? "" : "readonly ";
        w.Open($"public {readOnly}void Write({WriterParameter()})");
        if (TagEnum(variant) != null || resolvedTag is PrimitiveRef)
        {
            var wire = TagWire(variant)!;
            w.Line($"writer.Write{Method(wire)}(({PrimitiveType(wire.Kind)})Kind);");
        }
        else if (variant.Tag != null && stringTags)
        {
            Write(variant.Tag, "TagOf(Kind)", w, Context.Empty);
        }

        w.Line($"WriteContent({Pass("writer")});");
        w.Close();
        w.Line();

        w.Open($"public {readOnly}void WriteContent({WriterParameter()})");
        w.Open("switch (Kind)");
        foreach (var variantCase in variant.Cases)
        {
            w.Line($"case {CaseValue(variant, variantCase)}:");
            w.Indent();
            if (variantCase.Type != null) Write(variantCase.Type, Member(variantCase.Name, name), w, Context.Empty);
            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Line($"    throw new {options.ExceptionType}($\"Unknown {name} tag {{Kind}}\");");
        w.Close();
        w.Close();
        w.Close();
        return Header() + w;
    }

    // ------------------------------------------------------------------ registry

    /// <summary>
    /// The registry class; with protocols it holds a nested class per protocol (<c>Packets.LoginServerbound.Dispatch</c>).
    /// </summary>
    private string Registry()
    {
        var w = new CodeWriter();
        var registries = Registries(options.Registry);
        if (registries is [(null, var all)])
        {
            w.Line($"/// <summary>Packet id dispatch for the {all.Count} packets of '{Escape(Project.Settings.Name)}'.</summary>");
            w.Open($"public static partial class {options.Registry}");
            RegistryMembers(w, all);
            w.Close();
            return Header() + w;
        }

        w.Line($"/// <summary>Packet id dispatch for the {registries.Count} protocols of '{Escape(Project.Settings.Name)}'.</summary>");
        w.Open($"public static partial class {options.Registry}");
        for (var i = 0; i < registries.Count; i++)
        {
            var (protocol, packets) = registries[i];
            if (i > 0) w.Line();
            Doc(w, protocol!.Doc ?? $"Packet id dispatch for protocol {protocol.Name}.",
                ProtocolRemark(protocol, packets.Count));
            w.Open($"public static partial class {protocol.Name}");
            RegistryMembers(w, packets);
            w.Close();
        }

        w.Close();
        return Header() + w;
    }

    private void RegistryMembers(CodeWriter w, List<StructDef> packets)
    {
        w.Line("/// <summary>Reads the packet with the given id and passes it to the visitor. Returns false for unknown ids.</summary>");
        w.Open($"public static bool Dispatch<TVisitor>(uint id, {ReaderParameter}, ref TVisitor visitor) where TVisitor : {options.VisitorInterface}");
        w.Open("switch (id)");
        foreach (var packet in packets)
        {
            w.Open($"case {packet.Id}:");
            w.Line($"var packet = {Global(packet)}.Read({Pass("reader")});");
            w.Line("visitor.Visit(ref packet);");
            w.Line("return true;");
            w.Close();
        }

        w.Line("default:");
        w.Line("    return false;");
        w.Close();
        w.Close();
        w.Line();

        w.Line("/// <summary>The packet name for an id, or null.</summary>");
        w.Open("public static string? NameOf(uint id) => id switch");
        foreach (var packet in packets) w.Line($"{packet.Id} => \"{packet.Name}\",");
        w.Line("_ => null,");
        w.Close(";");
        w.Line();
        w.Line("/// <summary>All packet ids.</summary>");
        w.Line($"public static global::System.ReadOnlySpan<uint> Ids => [{string.Join(", ", packets.Select(p => p.Id))}];");
    }
}
