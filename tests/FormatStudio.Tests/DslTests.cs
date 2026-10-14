using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

public class DslTests
{
    private static TypeDef Read(string yaml)
    {
        var diagnostics = new DiagnosticBag();
        var definition = DslReader.Read(yaml, "test.yaml", diagnostics);
        Assert.True(definition != null, string.Join("\n", diagnostics));
        Assert.DoesNotContain(diagnostics, d => d.Severity != Severity.Info);
        return definition!;
    }

    /// <summary>Writing and reading again gives the same text.</summary>
    private static void AssertStable(TypeDef definition)
    {
        var text = DslWriter.Write(definition);
        Assert.Equal(text, DslWriter.Write(Read(text)));
    }

    [Theory]
    [InlineData("u8", "u8")]
    [InlineData("u16be", "u16be")]
    [InlineData("varuint32", "varuint32")]
    [InlineData("Foo", "Foo")]
    [InlineData("Foo[]", "Foo[]")]
    [InlineData("Foo[4]", "Foo[4]")]
    [InlineData("Foo[u16le]", "Foo[u16le]")]
    [InlineData("Foo[eof]", "Foo[eof]")]
    [InlineData("Foo[count * 2]", "Foo[count * 2]")]
    [InlineData("Foo?", "Foo?")]
    [InlineData("Foo[]?", "Foo[]?")]
    [InlineData("Foo?[]", "Foo?[]")]
    [InlineData("string[][]", "string[][]")]
    [InlineData("bytes", "bytes")]
    [InlineData("nbt", "nbt")]
    public void TypeExpressionsRoundTrip(string text, string expected)
    {
        Assert.Equal(expected, TypeExpressions.TryFormat(TypeExpressions.Parse(text)));
    }

    [Fact]
    public void TypeExpressionStructure()
    {
        var type = TypeExpressions.Parse("Foo[]?");
        var optional = Assert.IsType<OptionalRef>(type);
        var array = Assert.IsType<ArrayRef>(optional.Inner);
        Assert.Equal(LengthKind.Default, array.Length.Kind);
        Assert.Equal(new NamedRef("Foo"), array.Element);

        Assert.Equal(new PrimitiveRef(PrimitiveKind.U32, Endian.Big), TypeExpressions.Parse("u32be"));
        Assert.Throws<System.FormatException>(() => TypeExpressions.Parse("u8be"));
        Assert.Throws<System.FormatException>(() => TypeExpressions.Parse("Foo[[]"));
    }

    [Fact]
    public void MappingTypeForms()
    {
        Assert.Equal(new StringRef(StringEncoding.Mutf8, new LengthSpec(LengthKind.Prefix, new PrimitiveRef(PrimitiveKind.U16))),
            DslReader.ReadType("{string: mutf8, length: u16}"));
        Assert.Equal(new BytesRef(new LengthSpec(LengthKind.Fixed, Fixed: 16)), DslReader.ReadType("{bytes: 16}"));
        Assert.Equal(new FixedPointRef(new PrimitiveRef(PrimitiveKind.I32), 5), DslReader.ReadType("{fixed: i32, fraction: 5}"));
        Assert.Equal(new NbtRef(NbtFlavor.JavaNetwork), DslReader.ReadType("{nbt: java-network}"));
        Assert.Equal(new NamedRef("Mode", new PrimitiveRef(PrimitiveKind.ZigZag32)), DslReader.ReadType("{type: Mode, as: zigzag32}"));
        var map = Assert.IsType<MapRef>(DslReader.ReadType("{map: {key: string, value: i32}, length: u32}"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.U32), map.Length.Prefix);
        var sized = Assert.IsType<SizedRef>(DslReader.ReadType("{sized: Foo, length: varuint32}"));
        Assert.Equal(new NamedRef("Foo"), sized.Inner);
    }

    [Fact]
    public void PacketWithAllFieldOptions()
    {
        var packet = Assert.IsType<StructDef>(Read("""
            packet: Example
            id: 0x1F
            groups: [play, clientbound]
            title: Example Packet
            doc: |-
              Two lines
              of documentation.
            fields:
              - count: varuint32
              - name: items
                type: Item[]
                length: count
                maxItems: 100
              - name: flags
                type: u8
                min: 0
                max: 7
              - name: extra
                type: string
                if: "flags & 1"
                maxLength: 32
                pattern: "^[a-z]+$"
              - name: version
                type: u8
                const: 3
              - name: total
                type: varuint32
                computed: len(items)
              - name: mode
                type: Mode
                as: zigzag32
            """));
        Assert.True(packet.IsPacket);
        Assert.Equal(31, packet.Id);
        Assert.Equal(["play", "clientbound"], packet.Groups);
        Assert.Equal("Two lines\nof documentation.", packet.Doc);
        Assert.Equal(7, packet.Fields.Count);
        Assert.Equal("count", Assert.IsType<ArrayRef>(packet.Fields[1].Type).Length.Expression);
        Assert.Equal(100, packet.Fields[1].MaxItems);
        Assert.Equal("flags & 1", packet.Fields[3].If);
        Assert.Equal(3, packet.Fields[4].Const!.GetValue<long>());
        Assert.Equal("len(items)", packet.Fields[5].Computed);
        Assert.Equal(new NamedRef("Mode", new PrimitiveRef(PrimitiveKind.ZigZag32)), packet.Fields[6].Type);
        AssertStable(packet);
    }

    [Fact]
    public void EnumVariantAliasBitfield()
    {
        var enumDef = Assert.IsType<EnumDef>(Read("""
            enum: Mode
            type: i32
            allowUnknown: true
            values:
              survival: 0
              creative:
              adventure: {value: 5, doc: "Can't break blocks."}
              spectator:
            """));
        Assert.Equal([0L, 1, 5, 6], enumDef.Members.Select(m => m.Value));
        Assert.True(enumDef.AllowUnknown);
        AssertStable(enumDef);

        var variant = Assert.IsType<VariantDef>(Read("""
            variant: Shape
            tag: {type: ShapeKind, as: u8}
            cases:
              - name: circle
                tag: Circle
                type: f32
              - name: none
                tag: None
            """));
        Assert.Equal("Circle", variant.Cases[0].TagMember);
        Assert.Null(variant.Cases[1].Type);
        AssertStable(variant);

        AssertStable(Read("alias: Name\ntype: {string: utf8, length: u16}"));

        var bitfield = Assert.IsType<BitfieldDef>(Read("""
            bitfield: Position
            type: i64
            fields:
              - name: x
                bits: 26
                signed: true
              - name: z
                bits: 26
                signed: true
              - y: 12
            """));
        Assert.Equal(64, bitfield.Members.Sum(m => m.Bits));
        AssertStable(bitfield);
    }

    [Fact]
    public void UntilArraysAndStringTags()
    {
        var array = Assert.IsType<ArrayRef>(DslReader.ReadType("{array: Item, until: \"index == 255\"}"));
        Assert.Equal(new LengthSpec(LengthKind.Until, Expression: "index == 255"), array.Length);
        Assert.Null(TypeExpressions.TryFormat(array));
        AssertStable(Read("struct: S\nfields:\n  - name: items\n    type: {array: Item, until: \"!slot.more\"}\n"));

        var variant = Assert.IsType<VariantDef>(Read("variant: V\ntag: string\ncases:\n  - name: a\n    tag: minecraft:a\n    type: u8\n"));
        Assert.Equal("minecraft:a", variant.Cases[0].TagMember);
        AssertStable(variant);

        var diagnostics = new DiagnosticBag();
        Assert.Null(DslReader.Read("struct: S\nfields:\n  - name: string\n", "name.yaml", diagnostics));
        Assert.Contains("{name: name, type: T}", diagnostics[0].Message);
    }

    [Fact]
    public void IntegersKeepTheirBase()
    {
        const string packetText = """
            packet: P
            id: 0x1F
            fields:
              - name: magic
                type: u32
                const: 0xCAFE
              - name: mask
                type: u8
                default: 0b1010
                min: -0x10
                max: 200
            """;
        var packet = Assert.IsType<StructDef>(Read(packetText));
        Assert.Equal(NumberBase.Hex, packet.IdBase);
        Assert.Equal(0xCAFE, packet.Fields[0].Const!.GetValue<long>());
        Assert.Equal(NumberBase.Hex, packet.Fields[0].ConstBase);
        Assert.Equal(10, packet.Fields[1].Default!.GetValue<long>());
        Assert.Equal(NumberBase.Binary, packet.Fields[1].DefaultBase);
        Assert.Equal(-16, packet.Fields[1].Min);
        Assert.Equal(NumberBase.Hex, packet.Fields[1].MinBase);
        Assert.Equal(NumberBase.Decimal, packet.Fields[1].MaxBase);
        Assert.Equal(packetText.Replace("\r\n", "\n") + "\n", DslWriter.Write(packet));

        Assert.Equal(NumberBase.Decimal, Assert.IsType<StructDef>(Read("packet: Q\nid: 39\nfields: []\n")).IdBase);

        var enumDef = Assert.IsType<EnumDef>(Read("enum: E\ntype: u8\nflags: true\nvalues:\n  a: 0b1\n  b: 0x02\n  c: 4\n"));
        Assert.Equal([NumberBase.Binary, NumberBase.Hex, NumberBase.Decimal], enumDef.Members.Select(m => m.Base));
        Assert.Contains("a: 0b1\n  b: 0x02\n  c: 4\n", DslWriter.Write(enumDef));

        var variant = Assert.IsType<VariantDef>(Read("variant: V\ntag: u8\ncases:\n  - name: a\n    tag: 0x10\n  - name: b\n"));
        Assert.Equal(NumberBase.Hex, variant.Cases[0].TagBase);
        Assert.Equal(17, variant.Cases[1].TagValue);
        Assert.Contains("tag: 0x10", DslWriter.Write(variant));
        AssertStable(variant);
    }

    [Theory]
    [InlineData("0", 0L, NumberBase.Decimal, "0")]
    [InlineData("-42", -42L, NumberBase.Decimal, "-42")]
    [InlineData("0x1f", 31L, NumberBase.Hex, "0x1F")]
    [InlineData("0x7", 7L, NumberBase.Hex, "0x07")]
    [InlineData("-0x10", -16L, NumberBase.Hex, "-0x10")]
    [InlineData("0b0101", 5L, NumberBase.Binary, "0b101")]
    [InlineData("-0b1", -1L, NumberBase.Binary, "-0b1")]
    [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue, NumberBase.Hex, "0x7FFFFFFFFFFFFFFF")]
    [InlineData("-0x8000000000000000", long.MinValue, NumberBase.Hex, "-0x8000000000000000")]
    public void IntegerLiterals(string text, long value, NumberBase numberBase, string formatted)
    {
        Assert.True(YamlValues.TryParseInteger(text, out var parsed, out var parsedBase));
        Assert.Equal(value, parsed);
        Assert.Equal(numberBase, parsedBase);
        Assert.Equal(formatted, YamlValues.FormatInteger(value, numberBase));
        Assert.Equal(value, YamlValues.InterpretPlain(text)!.GetValue<long>());
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("0b2")]
    [InlineData("0x8000000000000000")]
    [InlineData("12abc")]
    public void InvalidIntegerLiterals(string text) => Assert.False(YamlValues.TryParseInteger(text, out _, out _));

    [Fact]
    public void ErrorsHaveLocations()
    {
        var diagnostics = new DiagnosticBag();
        Assert.Null(DslReader.Read("struct: Foo\nfields:\n  - name: a\n    type: u8[[\n", "bad.yaml", diagnostics));
        var error = Assert.Single(diagnostics);
        Assert.Equal("bad.yaml", error.Source!.File);
        Assert.Equal(4, error.Source.Line);

        diagnostics.Clear();
        Assert.Null(DslReader.Read("packet: A\nstruct: B\n", "two.yaml", diagnostics));
        Assert.Contains("exactly one", diagnostics[0].Message);

        diagnostics.Clear();
        Assert.NotNull(DslReader.Read("struct: A\nfeilds: []\n", "typo.yaml", diagnostics));
        Assert.Contains(diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("feilds"));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("0b10")]
    [InlineData("-0x10")]
    [InlineData("null")]
    [InlineData("a: b")]
    [InlineData("- item")]
    [InlineData("#comment")]
    [InlineData("flags & 1")]
    [InlineData("Foo[]")]
    [InlineData(" leading")]
    [InlineData("it's")]
    [InlineData("tab\there")]
    [InlineData("")]
    public void YamlScalarsSurviveQuoting(string text)
    {
        var yaml = YamlEmitter.Write(new YMap { { "key", text } });
        var parsed = YamlValues.ParseYaml(yaml)!["key"];
        Assert.Equal(text, parsed!.GetValue<string>());
    }

    [Fact]
    public void ProjectOverridesAndValidation()
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\nsources:\n  - base/*.yaml\n  - overrides/*.yaml\n");
        Directory.CreateDirectory(Path.Combine(directory, "base"));
        Directory.CreateDirectory(Path.Combine(directory, "overrides"));
        File.WriteAllText(Path.Combine(directory, "base", "A.yaml"), "struct: A\nfields:\n  - x: u8\n");
        File.WriteAllText(Path.Combine(directory, "base", "B.yaml"), "struct: B\nfields:\n  - a: A\n  - c: Missing\n");
        File.WriteAllText(Path.Combine(directory, "overrides", "A.yaml"), "struct: A\nfields:\n  - x: u16\n");

        var project = FormatProject.Load(directory);
        Assert.Equal(new PrimitiveRef(PrimitiveKind.U16), ((StructDef)project.Find("A")!).Fields[0].Type);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Info && d.Message.Contains("Overrides 'A'"));
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Unknown type 'Missing'"));
    }

    [Fact]
    public void ValidatorChecksExpressionsAndTags()
    {
        var project = FormatProject.InMemory([
            Read("struct: S\nfields:\n  - name: a\n    type: u8\n    if: b\n  - b: bool\n  - name: c\n    type: u8[later]\n  - later: u8\n"),
            Read("variant: V\ntag: u8\ncases:\n  - name: x\n    tag: 1\n  - name: y\n    tag: 1\n"),
            Read("variant: External\ncases:\n  - name: x\n"),
            Read("struct: UsesExternal\nfields:\n  - e: External\n"),
        ]);
        Validation.Validator.Validate(project);
        var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        Assert.Contains(errors, e => e.Contains("uses 'b', which is not an earlier field"));
        Assert.Contains(errors, e => e.Contains("uses 'later'"));
        Assert.Contains(errors, e => e.Contains("have the same tag 1"));
        Assert.Contains(errors, e => e.Contains("the use site needs 'tag'"));
    }

    [Theory]
    [InlineData("Foo[count * 2]")]
    [InlineData("{string: mutf8, length: u16}")]
    [InlineData("{string: default, length: zero}")]
    [InlineData("{array: {string: ascii, length: u8}, length: \"a[0]\"}")]
    [InlineData("{map: {key: string, value: \"Foo[4]\"}, length: eof}")]
    [InlineData("{optional: {sized: Foo, length: \"len, 2\"}}")]
    [InlineData("{type: Mode, as: zigzag32}")]
    [InlineData("{type: Data, tag: \"kind == 1 ? 2 : 3\"}")]
    [InlineData("{fixed: i32, fraction: 5}")]
    public void FormatTypeReadsBack(string text)
    {
        var type = DslReader.ReadType(text);
        var formatted = DslWriter.FormatType(type);
        Assert.DoesNotContain('\n', formatted);
        Assert.Equal(type, DslReader.ReadType(formatted));
    }

    [Fact]
    public void WithAddsADraftWithoutChangingTheProject()
    {
        var project = FormatProject.InMemory([Read("struct: A\nfields:\n  - x: u8\n")]);
        var draft = Read("struct: A\nfields:\n  - x: u16\n");
        var copy = project.With(draft);
        Assert.Same(draft, copy.Find("A"));
        Assert.NotSame(draft, project.Find("A"));
        Assert.Same(project.Settings, copy.Settings);
    }

    [Fact]
    public void ValidatorChecksUntilArraysAndStringTags()
    {
        var project = FormatProject.InMemory([
            Read("struct: Item\nfields:\n  - index: u8\n"),
            Read("struct: S\nfields:\n  - name: ok\n    type: {array: Item, until: \"index == 0\"}\n  - name: unknown\n    type: {array: Item, until: \"missing\"}\n" +
                 "  - name: primitive\n    type: {array: u8, until: \"x\"}\n"),
            Read("variant: Strings\ntag: string\ncases:\n  - name: a\n    tag: x\n  - name: b\n    tag: x\n  - name: c\n    tag: 3\n"),
            Read("variant: Numbers\ntag: u8\ncases:\n  - name: a\n    tag: text\n"),
        ]);
        Validation.Validator.Validate(project);
        var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        Assert.Contains(errors, e => e.Contains("The until expression uses 'missing'"));
        Assert.Contains(errors, e => e.Contains("needs struct elements"));
        Assert.Contains(errors, e => e.Contains("have the same tag x"));
        Assert.Contains(errors, e => e.Contains("Case c needs a string tag"));
        Assert.Contains(errors, e => e.Contains("needs an enum or a string tag type"));
        Assert.Equal(5, errors.Count);
    }
}
