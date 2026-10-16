using System.Reflection;
using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Projects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FormatStudio.Tests;

/// <summary>
/// The generator SDK: a generator for a language of its own, compiled into an assembly that sees only the public API
/// of Format Studio, built on <see cref="CodeEmitter"/>.
/// </summary>
public class GeneratorSdkTests
{
    /// <summary>"Sketch", an indented pseudo-language without braces or semicolons.</summary>
    private const string GeneratorSource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json.Nodes;
        using FormatStudio.Codegen;
        using FormatStudio.Model;
        using FormatStudio.Projects;

        namespace Sketch;

        public sealed class SketchGenerator : ICodeGenerator
        {
            public string Name => "sketch";
            public string Title => "Sketch";
            public string Language => "text";
            public string Description => "Pseudo-code";
            public string DefaultOutput => "generated/sketch";
            public string Marker => "# sketch";
            public IReadOnlyList<CodegenOption> Options { get; } = [CodegenOption.Bool("ranges", "Ranges", true, "Check min and max.")];

            public GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped) =>
                new SketchEmitter(project, skipped, new OptionReader(options, Options).Bool("ranges")).Run();
        }

        internal sealed class SketchEmitter(FormatProject project, IReadOnlySet<string> skipped, bool ranges) : CodeEmitter(project, skipped)
        {
            protected override string Generator => "sketch";
            protected override string Marker => "# sketch";
            protected override string NativeNamespace => "natives";
            protected override string RuntimeNamespace => "runtime";
            protected override string NativeFilePath(string fileName) => "natives/" + fileName;
            protected override string PathOf(TypeDef definition) => definition.Name + ".sketch";

            private string File(string kind, TypeDef definition, System.Action<CodeWriter> body)
            {
                var w = new CodeWriter();
                w.Line(Marker);
                w.Line($"{kind} {definition.Name}:");
                w.Indent();
                body(w);
                return w.ToString();
            }

            protected override string Struct(StructDef structDef) => File("struct", structDef, w =>
            {
                foreach (var field in structDef.Fields.Where(f => f.IsInput)) w.Line($"{field.Name}: {Describe(field.Type)}");
                w.Line("def read(reader):");
                w.Indent();
                w.Line("result = new()");
                ReadFields(structDef, w, f => f.IsInput ? "result." + f.Name : "_" + f.Name);
                w.Line("return result");
                w.Outdent();
                w.Line("def write(self, writer):");
                w.Indent();
                WriteFields(structDef, w, f => f.IsInput ? "self." + f.Name : "_" + f.Name);
                w.Outdent();
            });

            protected override string Enum(EnumDef enumDef) => File("enum", enumDef, w =>
            {
                foreach (var member in enumDef.Members) w.Line($"{member.Name} = {member.Value}");
            });

            protected override string Variant(VariantDef variant) => File("variant", variant, w =>
            {
                foreach (var variantCase in variant.Cases)
                {
                    w.Line($"case {variantCase.Name}:");
                    w.Indent();
                    w.Line(variantCase.Type == null ? "pass" : "value = " + Read(variantCase.Type, w, Context.Empty));
                    if (variantCase.Type != null) Write(variantCase.Type, "value", w, Context.Empty);
                    w.Outdent();
                }
            });

            protected override string Bitfield(BitfieldDef bitfield) => File("bitfield", bitfield, w => w.Line("..."));

            // Structs
            protected override void DeclareLocal(FieldDef field, string name, CodeWriter w) => w.Line($"{name} = none");
            protected override void OpenIf(string condition, CodeWriter w) { w.Line($"if {condition}:"); w.Indent(); }
            protected override void CloseIf(CodeWriter w) => w.Outdent();
            protected override void Assign(string target, string value, CodeWriter w) => w.Line($"{target} = {value}");
            protected override void CheckConstant(StructDef structDef, FieldDef field, string value, CodeWriter w) =>
                w.Line($"check {value} == {Constant(field)}");
            protected override void CheckRange(StructDef structDef, FieldDef field, string value, CodeWriter w)
            {
                if (ranges) w.Line($"check {field.Min?.ToString() ?? "-inf"} <= {value} <= {field.Max?.ToString() ?? "inf"}");
            }
            protected override void DeclareComputed(FieldDef field, string name, string code, ValueKind kind, CodeWriter w) =>
                w.Line($"{name} = {code}  # {kind}");
            protected override string RequirePresent(StructDef structDef, FieldDef field, string value, CodeWriter w)
            {
                w.Line($"require {value}");
                return value;
            }
            protected override string Constant(FieldDef field) => field.Const!.ToJsonString();

            // Reading
            protected override string Statement(string expression) => expression;
            protected override string ReadPrimitive(PrimitiveRef primitive, CodeWriter w, Context context) => $"{context.Reader}.{Method(primitive)}()";
            protected override string ReadString(StringRef str, CodeWriter w, Context context) =>
                $"{context.Reader}.utf8({(str.Length.Kind == LengthKind.Zero ? "zero" : ReadCount(str.Length, w, context, 1, null))})";
            protected override string ReadBytes(BytesRef bytes, CodeWriter w, Context context) =>
                $"{context.Reader}.bytes({(bytes.Length.Kind == LengthKind.Eof ? "end" : ReadCount(bytes.Length, w, context, 1, null))})";
            protected override string ReadArray(ArrayRef array, CodeWriter w, Context context)
            {
                var items = Temp("items");
                w.Line($"{items} = []");
                if (array.Length.Kind is LengthKind.Eof or LengthKind.Until)
                {
                    w.Line(array.Length.Kind == LengthKind.Eof ? $"while not {context.Reader}.end:" : "loop:");
                    w.Indent();
                    var item = Temp("item");
                    w.Line($"{item} = {Read(array.Element, w, context.Element())}");
                    w.Line($"{items}.add({item})");
                    if (array.Length.Kind == LengthKind.Until) w.Line($"if {Condition(array.Length.Expression!, ElementContext(array, item))}: break");
                    w.Outdent();
                    return items;
                }

                w.Line($"repeat {ReadCount(array.Length, w, context, MinSize(array.Element), null)}:");
                w.Indent();
                w.Line($"{items}.add({Read(array.Element, w, context.Element())})");
                w.Outdent();
                return items;
            }
            protected override string ReadMap(MapRef map, CodeWriter w, Context context)
            {
                var entries = Temp("map");
                w.Line($"{entries} = {{}}");
                w.Line($"repeat {ReadCount(map.Length, w, context, MinSize(map.Key), null)}:");
                w.Indent();
                var key = Temp("key");
                w.Line($"{key} = {Read(map.Key, w, context.Element())}");
                w.Line($"{entries}[{key}] = {Read(map.Value, w, context.Element())}");
                w.Outdent();
                return entries;
            }
            protected override string ReadOptional(OptionalRef optional, CodeWriter w, Context context)
            {
                var value = Temp("optional");
                w.Line($"{value} = none");
                OpenIf($"{context.Reader}.Bool()", w);
                w.Line($"{value} = {Read(optional.Inner, w, context.Element())}");
                CloseIf(w);
                return value;
            }
            protected override string ReadSized(SizedRef sized, CodeWriter w, Context context)
            {
                var sub = Temp("reader");
                w.Line($"{sub} = {context.Reader}.slice({ReadCount(sized.Length, w, context, 1, null)})");
                return Read(sized.Inner, w, context.WithReader(sub));
            }
            protected override string ReadFixedPoint(FixedPointRef fixedPoint, CodeWriter w, Context context) =>
                $"{context.Reader}.{Method(fixedPoint.Base)}() / 2^{fixedPoint.FractionBits}";
            protected override string ReadEnum(NamedRef named, EnumDef enumDef, CodeWriter w, Context context) =>
                $"{enumDef.Name}({context.Reader}.{Method(named.As ?? enumDef.Type)}())";
            protected override string ReadTaggedVariant(NamedRef named, VariantDef variant, CodeWriter w, Context context) =>
                $"{variant.Name}.read({context.Reader}, tag={Expression(named.Tag!, context, out _)})";
            protected override string ReadDefinition(TypeDef definition, CodeWriter w, Context context) => $"{definition.Name}.read({context.Reader})";
            protected override string ReadLengthPrefix(PrimitiveRef prefix, Context context) => $"{context.Reader}.{Method(prefix)}()";
            protected override string CheckCount(string raw, int minSize, int? max, CodeWriter w, Context context)
            {
                var count = Temp("count");
                w.Line($"{count} = check_count({raw}, {minSize})");
                return count;
            }

            // Writing
            protected override void WritePrimitive(PrimitiveRef primitive, string value, CodeWriter w, Context context) =>
                w.Line($"{context.Writer}.{Method(primitive)}({value})");
            protected override void WriteString(StringRef str, string value, CodeWriter w, Context context)
            {
                WriteCount(str.Length, $"utf8_length({value})", w, context, context.Field?.Name ?? "value");
                w.Line($"{context.Writer}.utf8({value})");
            }
            protected override void WriteBytes(BytesRef bytes, string value, CodeWriter w, Context context)
            {
                WriteCount(bytes.Length, $"len({value})", w, context, context.Field?.Name ?? "value");
                w.Line($"{context.Writer}.bytes({value})");
            }
            protected override void WriteArray(ArrayRef array, string value, CodeWriter w, Context context)
            {
                WriteCount(array.Length, $"len({value})", w, context, context.Field?.Name ?? "value");
                var item = Temp("item");
                w.Line($"for {item} in {value}:");
                w.Indent();
                Write(array.Element, item, w, context.Element());
                w.Outdent();
            }
            protected override void WriteMap(MapRef map, string value, CodeWriter w, Context context)
            {
                WriteCount(map.Length, $"len({value})", w, context, context.Field?.Name ?? "value");
                var key = Temp("key");
                w.Line($"for {key} in {value}:");
                w.Indent();
                Write(map.Key, key, w, context.Element());
                Write(map.Value, $"{value}[{key}]", w, context.Element());
                w.Outdent();
            }
            protected override void WriteOptional(OptionalRef optional, string value, CodeWriter w, Context context)
            {
                w.Line($"{context.Writer}.Bool({value} != none)");
                OpenIf($"{value} != none", w);
                Write(optional.Inner, value, w, context.Element());
                CloseIf(w);
            }
            protected override void WriteSized(SizedRef sized, string value, CodeWriter w, Context context)
            {
                var sub = Temp("writer");
                w.Line($"{sub} = buffer()");
                Write(sized.Inner, value, w, context.WithWriter(sub));
                WriteCount(sized.Length, $"len({sub})", w, context, "value");
                w.Line($"{context.Writer}.bytes({sub})");
            }
            protected override void WriteFixedPoint(FixedPointRef fixedPoint, string value, CodeWriter w, Context context) =>
                w.Line($"{context.Writer}.{Method(fixedPoint.Base)}(round({value} * 2^{fixedPoint.FractionBits}))");
            protected override void WriteEnum(NamedRef named, EnumDef enumDef, string value, CodeWriter w, Context context) =>
                w.Line($"{context.Writer}.{Method(named.As ?? enumDef.Type)}({value})");
            protected override void WriteTaggedVariant(NamedRef named, VariantDef variant, string value, CodeWriter w, Context context) =>
                w.Line($"{value}.write_content({context.Writer})");
            protected override void WriteDefinition(TypeDef definition, string value, CodeWriter w, Context context) =>
                w.Line($"{value}.write({context.Writer})");
            protected override void WriteLengthPrefix(PrimitiveRef prefix, string count, CodeWriter w, Context context) =>
                w.Line($"{context.Writer}.{Method(prefix)}({count})");
            protected override void CheckFixedCount(string count, int length, string what, CodeWriter w, Context context) =>
                w.Line($"check {count} == {length}  # {what}");

            // Expressions
            protected override string MemberName(string name, string owner) => name;
            protected override string NumberLiteral(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            protected override string StringLiteral(string text) => System.Text.Json.JsonSerializer.Serialize(text);
            protected override Value EnumConstant(EnumDef enumDef, string member) =>
                new($"{enumDef.Name}.{member}", $"{enumDef.Name}.{member}", ValueKind.Number, new NamedRef(enumDef.QualifiedName));
            protected override string Convert(string access, ValueKind kind, TypeRef declared, bool mayBeNull) => mayBeNull ? $"({access} or 0)" : access;
            protected override string IndexAccess(string target, string index) => $"{target}[{index}]";
            protected override string Length(Value value) => $"len({value.Raw})";
            protected override string ByteLength(Value value) => $"utf8_length({value.Raw})";
            protected override string TagNumber(Value value) => $"tag({value.Raw})";
            protected override string TagText(VariantDef variant, Value value) => $"tag({value.Raw})";
            protected override string TextCompare(string left, string right, string op) => $"({left} {op} {right})";
        }
        """;

    private static readonly Lazy<Assembly> Plugin = new(() =>
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        // Another assembly name than FormatStudio.Tests, so that the internals of Format Studio are out of reach.
        var compilation = CSharpCompilation.Create("Sketch", [CSharpSyntaxTree.ParseText(GeneratorSource)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(stream.ToArray());
    });

    private static ICodeGenerator Sketch() => (ICodeGenerator)Activator.CreateInstance(Plugin.Value.GetType("Sketch.SketchGenerator")!)!;

    private static FormatProject Project(params (string File, string Text)[] types)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\ndefaults:\n  length: varuint32\nsources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        foreach (var (file, text) in types) File.WriteAllText(Path.Combine(directory, "types", file), text);
        var project = FormatProject.Load(directory);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        return project;
    }

    [Fact]
    public void AGeneratorOutsideFormatStudioWalksStructs()
    {
        var project = Project(
            ("Color.yaml", "enum: Color\ntype: u8\nvalues:\n  red: 0\n  green: 1\n"),
            ("Header.yaml", "struct: Header\nfields:\n  - {name: magic, type: u16, const: 0xCAFE}\n  - {name: count, type: varint32, computed: len(items)}\n" +
                            "  - kind: Color\n  - {name: items, type: \"i32[count]\"}\n  - {name: note, type: string, if: \"kind == Color::green\"}\n" +
                            "  - {name: level, type: u8, min: 1, max: 9}\n  - scores: {map: {key: string, value: i16}}\n  - maybe: u32?\n" +
                            "  - inner: {sized: Point, length: u16}\n  - pos: {fixed: i32, fraction: 5}\n  - tail: bytes[eof]\n"),
            ("Point.yaml", "struct: Point\nfields:\n  - x: i16\n  - y: i16\n"));

        var output = Sketch().Generate(project, new JsonObject(), new HashSet<string>());

        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        Assert.Equal("""
            # sketch
            struct Header:
                kind: Color
                items: i32[count]
                note: string
                level: u8
                scores: map<string(default, default), i16>[default]
                maybe: u32?
                inner: sized(Point, u16)
                pos: fixed(i32, 5)
                tail: bytes[eof]
                def read(reader):
                    result = new()
                    _magic = none
                    _magic = reader.U16LE()
                    check _magic == 51966
                    _count = none
                    _count = reader.VarInt32()
                    result.kind = Color(reader.U8())
                    __items0 = []
                    __count1 = check_count(_count, 4)
                    repeat __count1:
                        __items0.add(reader.I32LE())
                    result.items = __items0
                    if (result.kind == Color.green):
                        __count2 = check_count(reader.VarUInt32(), 1)
                        result.note = reader.utf8(__count2)
                    result.level = reader.U8()
                    check 1 <= result.level <= 9
                    __map3 = {}
                    __count4 = check_count(reader.VarUInt32(), 0)
                    repeat __count4:
                        __count6 = check_count(reader.VarUInt32(), 1)
                        __key5 = reader.utf8(__count6)
                        __map3[__key5] = reader.I16LE()
                    result.scores = __map3
                    __optional7 = none
                    if reader.Bool():
                        __optional7 = reader.U32LE()
                    result.maybe = __optional7
                    __count9 = check_count(reader.U16LE(), 1)
                    __reader8 = reader.slice(__count9)
                    result.inner = Point.read(__reader8)
                    result.pos = reader.I32LE() / 2^5
                    result.tail = reader.bytes(end)
                    return result
                def write(self, writer):
                    _count = len(self.items)  # Number
                    writer.U16LE(51966)
                    writer.VarInt32(_count)
                    writer.U8(self.kind)
                    for __item10 in self.items:
                        writer.I32LE(__item10)
                    if (self.kind == Color.green):
                        require self.note
                        writer.VarUInt32(utf8_length(self.note))
                        writer.utf8(self.note)
                    writer.U8(self.level)
                    writer.VarUInt32(len(self.scores))
                    for __key11 in self.scores:
                        writer.VarUInt32(utf8_length(__key11))
                        writer.utf8(__key11)
                        writer.I16LE(self.scores[__key11])
                    writer.Bool(self.maybe != none)
                    if self.maybe != none:
                        writer.U32LE(self.maybe)
                    __writer12 = buffer()
                    self.inner.write(__writer12)
                    writer.U16LE(len(__writer12))
                    writer.bytes(__writer12)
                    writer.I32LE(round(self.pos * 2^5))
                    writer.bytes(self.tail)
            """.Replace("\r", ""), output.FileOf("Header")!.Content.TrimEnd());
    }

    [Fact]
    public void ExpressionsAndVariantsGoThroughTheHooks()
    {
        var project = Project(
            ("Item.yaml", "struct: Item\nfields:\n  - more: bool\n  - value: i16\n"),
            ("Data.yaml", "variant: Data\ntag: u8\ncases:\n  - {name: text, tag: 1, type: string}\n  - {name: none, tag: 2}\n"),
            ("A.yaml", "struct: A\nfields:\n  - name: items\n    type: {array: Item, until: \"!more\"}\n  - kind: u8\n" +
                       "  - name: data\n    type: Data\n    tag: kind\n  - {name: check, type: u8, computed: \"tagof(data) * 2\"}\n"));

        var output = Sketch().Generate(project, new JsonObject(), new HashSet<string>());

        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        var a = output.FileOf("A")!.Content;
        Assert.Contains("if !__item1.more: break", a);
        Assert.Contains("result.data = Data.read(reader, tag=result.kind)", a);
        Assert.Contains("_check = (tag(self.data) * 2)  # Number", a);
        Assert.Contains("self.data.write_content(writer)", a);
        Assert.Contains("    case text:\n        __count0 = check_count(reader.VarUInt32(), 1)\n        value = reader.utf8(__count0)\n", output.FileOf("Data")!.Content);
    }

    [Fact]
    public void ErrorsOfTheLanguageAreReportedPerDefinition()
    {
        var project = Project(("A.yaml", "struct: A\nfields:\n  - {name: x, type: u8, if: \"_root != null\"}\n"),
            ("B.yaml", "struct: B\nfields:\n  - y: u8\n"));

        var output = Sketch().Generate(project, new JsonObject(), new HashSet<string>());

        Assert.Contains(output.Diagnostics, d => d.Message.Contains("A: _parent and _root are not supported", StringComparison.Ordinal));
        Assert.Null(output.FileOf("A"));
        Assert.NotNull(output.FileOf("B"));
    }
}
