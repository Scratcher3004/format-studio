using System.Text.Json.Nodes;
using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Tests;

public class CodecTests
{
    private static FormatProject Project(Endian endian, params string[] definitions)
    {
        var parsed = definitions.Select(yaml => DslReader.Read(yaml, "test.yaml", []) ?? throw new InvalidOperationException(yaml));
        var project = FormatProject.InMemory(parsed, new ProjectSettings { Defaults = { Endian = endian } });
        Validation.Validator.Validate(project);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        return project;
    }

    private static string Encode(FormatProject project, string type, string json)
    {
        var result = new Codec(project).Encode(type, JsonNode.Parse(json));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Hex.Format(result.Bytes);
    }

    private static JsonNode? Decode(FormatProject project, string type, string hex)
    {
        var result = new Codec(project).Decode(type, Hex.Parse(hex));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(Hex.Parse(hex).Length, result.Consumed);
        return result.Value;
    }

    [Theory]
    [InlineData("varuint32", "0", "00")]
    [InlineData("varuint32", "300", "ac02")]
    [InlineData("varuint32", "4294967295", "ffffffff0f")]
    [InlineData("varint32", "-1", "ffffffff0f")]
    [InlineData("zigzag32", "-1", "01")]
    [InlineData("zigzag32", "1", "02")]
    [InlineData("zigzag32", "-2147483648", "ffffffff0f")]
    [InlineData("zigzag64", "-3", "05")]
    [InlineData("varuint64", "18446744073709551615", "ffffffffffffffffff01")]
    [InlineData("u16", "4660", "3412")]
    [InlineData("u16be", "4660", "1234")]
    [InlineData("i32", "-2", "feffffff")]
    [InlineData("f16", "1.5", "003e")]
    [InlineData("f16be", "-2", "c000")]
    [InlineData("f16", "65504", "ff7b")]
    [InlineData("f16", "5.960464477539063E-08", "0100")]
    [InlineData("f32", "1.5", "0000c03f")]
    [InlineData("f64be", "1", "3ff0000000000000")]
    [InlineData("bool", "true", "01")]
    [InlineData("string", "\"hé\"", "0368c3a9")]
    [InlineData("bytes", "\"0a0b\"", "020a0b")]
    [InlineData("u8[3]", "[1,2,3]", "010203")]
    [InlineData("u8[u16be]", "[7]", "000107")]
    [InlineData("u8?", "null", "00")]
    [InlineData("u8?", "9", "0109")]
    public void Primitives(string type, string json, string hex)
    {
        var project = Project(Endian.Little);
        Assert.Equal(hex, Encode(project, type, json));
        Assert.True(JsonValues.DeepEquals(JsonNode.Parse(json), Decode(project, type, hex)));
    }

    [Fact]
    public void F16RoundsToNearest()
    {
        var project = Project(Endian.Little);
        Assert.Equal("553e", Encode(project, "f16", "1.583"));
        Assert.Equal("007c", Encode(project, "f16", "100000"));
    }

    [Fact]
    public void PaddedStrings()
    {
        var project = Project(Endian.Little,
            "alias: Spaced\ntype: {string: ascii, length: 5, pad: \" \"}\n",
            "alias: Zeroed\ntype: {string: utf8, length: 4, pad: zero}\n",
            "alias: CName\ntype: {string: utf8, length: 4, pad: terminated}\n",
            "alias: Wide\ntype: {string: utf16le, length: 6, pad: terminated}\n",
            "struct: Sized\nfields:\n  - size: u8\n  - name: text\n    type: string\n    length: size\n    pad: terminated\n");
        Assert.Equal("6162202020", Encode(project, "Spaced", "\"ab\""));
        Assert.Equal("\"ab\"", Decode(project, "Spaced", "6162202020")!.ToJsonString());
        Assert.Equal("\" a\"", Decode(project, "Spaced", "2061202020")!.ToJsonString());
        Assert.Equal("61620000", Encode(project, "Zeroed", "\"ab\""));
        Assert.Equal("\"a\\u0000b\"", Decode(project, "Zeroed", "61006200")!.ToJsonString());

        // Terminated: the string ends at the first NUL, whatever follows; a string that fills the field has no NUL.
        Assert.Equal("61620000", Encode(project, "CName", "\"ab\""));
        Assert.Equal("\"a\"", Decode(project, "CName", "6100ffff")!.ToJsonString());
        Assert.Equal("61626364", Encode(project, "CName", "\"abcd\""));
        Assert.Equal("\"abcd\"", Decode(project, "CName", "61626364")!.ToJsonString());
        Assert.Equal("\"\"", Decode(project, "CName", "00626364")!.ToJsonString());
        Assert.Equal("\"a\\u0100\"", Decode(project, "Wide", "610000010000")!.ToJsonString());
        Assert.Equal("610000000000", Encode(project, "Wide", "\"a\""));

        Assert.Equal("03616200", Encode(project, "Sized", """{"size": 3, "text": "ab"}"""));
        Assert.Equal("""{"size":2,"text":"ab"}""", Decode(project, "Sized", "026162")!.ToJsonString());

        var codec = new Codec(project);
        Assert.False(codec.Encode("CName", JsonValue.Create("abcde")).Success);
        Assert.False(codec.Encode("CName", JsonValue.Create("a\0b")).Success);
        Assert.False(codec.Encode("Sized", JsonNode.Parse("""{"size": 1, "text": "ab"}""")).Success);
        var trailing = codec.Encode("Spaced", JsonValue.Create("ab "));
        Assert.True(trailing.Success);
        Assert.Contains(trailing.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("pad character"));
    }

    [Fact]
    public void PaddingIsAChildOfTheString()
    {
        var project = Project(Endian.Little, "struct: A\nfields:\n  - {name: name, type: {string: ascii, length: 6, pad: terminated}}\n");
        var result = new Codec(project).Decode("A", Hex.Parse("616200ffff00"));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var padding = Assert.Single(result.Root.Children.Single(c => c.Name == "name").Children);
        Assert.Equal(("padding", "name.padding", 2, 4, "00ffff00"), (padding.Name, padding.Path, padding.Offset, padding.Length, padding.Display));
        Assert.Empty(new Codec(project).Decode("A", Hex.Parse("616263646566")).Root.Children.Single().Children);
    }

    [Theory]
    [InlineData("", "warn")]
    [InlineData("\n  check: strict", "")]
    [InlineData(", check: ignore", "ignore")]
    [InlineData(", check: warn", "warn")]
    [InlineData(", check: strict", "strict")]
    public void TerminatedPaddingChecks(string defaults, string check)
    {
        var settings = defaults.StartsWith('\n') ? defaults[1..] : null;
        var type = check == "" ? "{string: ascii, length: 4, pad: terminated}" : $"{{string: ascii, length: 4, pad: terminated, check: {check}}}";
        var parsed = DslReader.Read($"struct: A\nfields:\n  - name: name\n    type: {type}\n", "test.yaml", []) ?? throw new InvalidOperationException();
        var project = FormatProject.InMemory([parsed], new ProjectSettings
        {
            Defaults = { Check = settings != null ? TypeExpressions.ParseCheck(settings.Split(": ")[1]) : PaddingCheck.Warn },
        });
        var effective = check == "" ? "strict" : check;
        var codec = new Codec(project);

        var garbage = codec.Decode("A", Hex.Parse("6100ff00"));
        Assert.Equal(effective != "strict", garbage.Success);
        Assert.Equal(effective == "warn", garbage.Diagnostics.Any(d => d.Severity == Severity.Warning && d.Message.Contains("Padding byte 0xff at offset 2")));
        if (effective == "strict") Assert.Contains(garbage.Diagnostics, d => d.Message.Contains("At offset 2: Padding byte 0xff") && d.Path == "name");
        Assert.True(codec.Decode("A", Hex.Parse("61000000")).Diagnostics.Count == 0);
    }

    [Fact]
    public void StrictPaddedStringsRejectATrailingPadCharacter()
    {
        var project = Project(Endian.Little, "alias: Code\ntype: {string: ascii, length: 4, pad: \" \", check: strict}\n");
        var result = new Codec(project).Encode("Code", JsonValue.Create("ab "));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("would be dropped when read"));
        Assert.Equal("61622020", Encode(project, "Code", "\"ab\""));
    }

    [Fact]
    public void CheckNeedsPadding()
    {
        var project = FormatProject.InMemory([DslReader.Read("alias: P\ntype: {string: utf8, length: 4, check: strict}\n", "test.yaml", [])!]);
        Validation.Validator.Validate(project);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("'check' only applies to padded strings"));
    }

    [Fact]
    public void FilledRegions()
    {
        var project = Project(Endian.Little,
            "alias: Mccs\ntype: {sized: {string: ascii, length: u8}, length: 8, fill: zero}\n",
            "alias: StrictMccs\ntype: {sized: {string: ascii, length: u8}, length: 8, fill: zero, check: strict}\n",
            "alias: Pattern\ntype: {sized: u8, length: 5, fill: 0xdead}\n",
            "struct: Block\nfields:\n  - size: u8\n  - {name: data, type: {sized: u16be, length: size, fill: 0xff}}\n");
        Assert.Equal("0361626300000000", Encode(project, "Mccs", "\"abc\""));
        Assert.Equal("0761626364656667", Encode(project, "Mccs", "\"abcdefg\""));
        Assert.Equal("\"abc\"", Decode(project, "Mccs", "0361626300000000")!.ToJsonString());
        Assert.Equal("01deaddead", Encode(project, "Pattern", "1"));
        Assert.Equal("040001ffff", Encode(project, "Block", """{"size": 4, "data": 1}"""));
        Assert.Equal("""{"size":2,"data":1}""", Decode(project, "Block", "020001")!.ToJsonString());

        var codec = new Codec(project);
        var tooLong = codec.Encode("Mccs", JsonValue.Create("abcdefgh"));
        Assert.Contains(tooLong.Diagnostics, d => d.Message.Contains("The value takes 9 bytes, more than the 8 of its region"));

        var decoded = codec.Decode("Mccs", Hex.Parse("03616263ff000000"));
        Assert.True(decoded.Success);
        Assert.Contains(decoded.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("Byte 0xff at offset 4 in the fill"));
        var padding = Assert.Single(decoded.Root.Children, c => c.Name == "padding");
        Assert.Equal((4, 4, "ff000000"), (padding.Offset, padding.Length, padding.Display));
        var strict = codec.Decode("StrictMccs", Hex.Parse("03616263ff000000"));
        Assert.Contains(strict.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("At offset 4: Byte 0xff in the fill"));
        Assert.Contains(codec.Decode("Pattern", Hex.Parse("01deadbeef")).Diagnostics, d => d.Message.Contains("Byte 0xbe at offset 3"));
    }

    [Theory]
    [InlineData("zero", "00")]
    [InlineData("255", "ff")]
    [InlineData("0xFF", "ff")]
    [InlineData("0x7", "07")]
    [InlineData("0xdead", "dead")]
    [InlineData("0xabc", null)]
    [InlineData("256", null)]
    [InlineData("ff", null)]
    public void FillPatterns(string text, string? hex)
    {
        if (hex == null) Assert.Throws<System.FormatException>(() => TypeExpressions.ParseFill(text));
        else Assert.Equal(hex, TypeExpressions.ParseFill(text));
    }

    [Theory]
    [InlineData("{sized: u8, length: u8, fill: zero}", "'fill' needs a fixed length")]
    [InlineData("{sized: u8, length: 4, check: strict}", "'check' on a sized value needs 'fill'")]
    [InlineData("{sized: \"bytes[eof]\", length: 4, fill: zero}", "never any fill")]
    public void FillChecks(string type, string message)
    {
        var project = FormatProject.InMemory([DslReader.Read($"alias: P\ntype: {type}\n", "test.yaml", [])!]);
        Validation.Validator.Validate(project);
        Assert.Contains(project.Diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void AsciiAndLatin1AreStrict()
    {
        var project = Project(Endian.Little, "alias: A\ntype: {string: ascii, length: u8}\n", "alias: L\ntype: {string: latin1, length: u8}\n");
        var codec = new Codec(project);
        Assert.Contains(codec.Decode("A", Hex.Parse("0180")).Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(codec.Encode("A", JsonValue.Create("\u00e9")).Diagnostics, d => d.Message.Contains("can't be written as ascii"));
        Assert.Equal("01e9", Encode(project, "L", "\"\u00e9\""));
        Assert.Equal("\u00e9", Decode(project, "L", "01e9")!.GetValue<string>());
        Assert.Contains(codec.Encode("L", JsonValue.Create("\u20ac")).Diagnostics, d => d.Message.Contains("can't be written as latin1"));
    }

    [Fact]
    public void StringsWithAMax()
    {
        var project = Project(Endian.Little, "alias: Name\ntype: {string: utf8, max: 3}\n", "alias: Code\ntype: {string: ascii, length: u8, max: 2}\n",
            "alias: CName\ntype: {string: utf8, length: zero, max: 3}\n");
        var codec = new Codec(project);
        Assert.Equal("03616263", Encode(project, "Name", "\"abc\""));
        Assert.Equal("06c3a9c3a9c3a9", Encode(project, "Name", "\"\u00e9\u00e9\u00e9\""));
        Assert.Contains(codec.Encode("Name", JsonValue.Create("abcd")).Diagnostics, d => d.Message.Contains("4 characters, more than its max 3"));
        Assert.Contains(codec.Decode("Name", Hex.Parse("0461626364")).Diagnostics, d => d.Message.Contains("4 characters, more than its max 3"));
        Assert.Contains(codec.Decode("Name", Hex.Parse("0a" + new string('6', 20))).Diagnostics,
            d => d.Message.Contains("10 bytes are more than a string of at most 3 characters takes"));
        Assert.Contains(codec.Decode("Code", Hex.Parse("03616263")).Diagnostics, d => d.Message.Contains("3 bytes are more than"));
        Assert.Contains(codec.Decode("CName", Hex.Parse("6162636400")).Diagnostics, d => d.Message.Contains("4 characters"));
        Assert.Equal("{string: default, max: 4}", DslWriter.FormatType(DslReader.ReadType("{string: default, max: 4}")));
    }

    [Fact]
    public void PaddedWritesStayWithinTheLimit()
    {
        var project = Project(Endian.Little, "struct: A\nfields:\n  - size: u32\n  - {name: name, type: {string: utf8, length: size, pad: zero}}\n");
        var result = new Codec(project) { MaxCount = 1000 }.Encode("A", JsonNode.Parse("""{"size": 4000000000, "name": "a"}"""));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("exceeds the limit of 1000"));
    }

    [Theory]
    [InlineData("{string: utf16le, length: 5}", true, true)]
    [InlineData("{string: utf16be, length: 6, pad: terminated}", false, true)]
    [InlineData("{string: ascii, length: 4}", false, false)]
    [InlineData("{string: mutf8, length: 4}", false, true)]
    [InlineData("{string: latin1, length: 4, pad: \"\u00e9\"}", false, false)]
    [InlineData("{string: utf8, length: 4, pad: \"\u00e9\"}", false, true)]
    [InlineData("{string: utf8, length: 4, pad: \" \"}", false, false)]
    public void StringChecks(string type, bool error, bool codegenWarning)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"),
            "name: p\nsources: [types/*.yaml]\ncodegen:\n  - {name: cs, generator: csharp, output: gen}\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"), $"alias: A\ntype: {type}\n");
        var project = FormatProject.Load(directory);
        Assert.Equal(error, project.Diagnostics.Any(d => d.Severity == Severity.Error && d.Message.Contains("even number of bytes")));
        Assert.Equal(codegenWarning, project.Diagnostics.Any(d => d.Severity == Severity.Warning && d.Message.Contains("Code generation (cs)")));
    }

    [Fact]
    public void PaddedStringsNeedAFieldSize()
    {
        var project = FormatProject.InMemory([DslReader.Read("alias: P\ntype: {string: utf8, length: u8, pad: zero}\n", "test.yaml", [])!]);
        Validation.Validator.Validate(project);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("padded string"));
        var diagnostics = new DiagnosticBag();
        Assert.Null(DslReader.Read("alias: P\ntype: {string: utf8, length: 4, pad: ab}\n", "test.yaml", diagnostics));
        Assert.Contains(diagnostics, d => d.Message.Contains("Unknown pad 'ab'"));
    }

    [Fact]
    public void UuidByteOrders()
    {
        var project = Project(Endian.Little);
        const string uuid = "\"00112233-4455-6677-8899-aabbccddeeff\"";
        Assert.Equal("00112233445566778899aabbccddeeff", Encode(project, "uuidbe", uuid));
        Assert.Equal("7766554433221100ffeeddccbbaa9988", Encode(project, "uuidle", uuid));
    }

    [Fact]
    public void RejectsOutOfRangeAndTruncatedData()
    {
        var project = Project(Endian.Little);
        var codec = new Codec(project);
        Assert.False(codec.Encode("u8", JsonValue.Create(256)).Success);
        Assert.False(codec.Encode("u8[2]", JsonNode.Parse("[1]")).Success);

        var truncated = codec.Decode("u32", [1, 2]);
        Assert.False(truncated.Success);
        Assert.NotNull(truncated.Root.Error);

        var hugeCount = codec.Decode("u32[]", Hex.Parse("ffffffff0f"));
        Assert.False(hugeCount.Success);
        Assert.Contains("4294967295", hugeCount.Diagnostics[0].Message);
    }

    [Fact]
    public void StructWithComputedCountAndConditions()
    {
        var project = Project(Endian.Big,
            "enum: Kind\ntype: u8\nvalues:\n  plain: 0\n  named: 1\n",
            """
            struct: Entry
            fields:
              - kind: Kind
              - name: name
                type: string
                if: kind == Kind::named
              - name: count
                type: varuint32
                computed: len(values)
              - name: values
                type: i16[count]
              - name: magic
                type: u8
                const: 42
            """);
        var hex = Encode(project, "Entry", """{"kind": "named", "name": "ab", "values": [1, -1]}""");
        Assert.Equal("01" + "026162" + "02" + "0001ffff" + "2a", hex);
        var decoded = Decode(project, "Entry", hex)!;
        Assert.Equal("named", decoded["kind"]!.GetValue<string>());
        Assert.Equal(2, decoded["count"]!.GetValue<long>());

        Assert.Equal("00" + "00" + "2a", Encode(project, "Entry", """{"kind": "plain", "values": []}"""));
        var decodedPlain = Decode(project, "Entry", "00002a")!.AsObject();
        Assert.False(decodedPlain.ContainsKey("name"));
    }

    [Fact]
    public void VariantsWithOwnAndExternalTags()
    {
        var project = Project(Endian.Little,
            "enum: ShapeKind\ntype: u8\nvalues:\n  none: 0\n  circle: 3\n",
            "variant: Shape\ntag: {type: ShapeKind, as: varuint32}\ncases:\n  - name: none\n    tag: none\n  - name: circle\n    tag: circle\n    type: f32\n",
            "variant: Payload\ncases:\n  - name: number\n    tag: 1\n    type: zigzag32\n  - name: text\n    tag: 2\n    type: string\n",
            "struct: Message\nfields:\n  - kind: u8\n  - name: payload\n    type: Payload\n    tag: kind\n");
        Assert.Equal("030000803f", Encode(project, "Shape", """{"circle": 1}"""));
        Assert.Equal("00", Encode(project, "Shape", "\"none\""));
        Assert.Equal("""{"circle":1}""", Decode(project, "Shape", "030000803f")!.ToJsonString());

        Assert.Equal("02" + "016b", Encode(project, "Message", """{"kind": 2, "payload": {"text": "k"}}"""));
        Assert.Equal("""{"kind":1,"payload":{"number":-2}}""", Decode(project, "Message", "0103")!.ToJsonString());
        Assert.False(new Codec(project).Encode("Message", JsonNode.Parse("""{"kind": 1, "payload": {"text": "k"}}""")).Success);
        Assert.False(new Codec(project).Decode("Message", Hex.Parse("0500")).Success);
    }

    [Fact]
    public void StringTags()
    {
        var project = Project(Endian.Big,
            "alias: Id\ntype: string\n",
            "variant: Source\ntag: Id\ncases:\n  - name: block\n    tag: minecraft:block\n    type: i32\n  - name: none\n    tag: minecraft:none\n",
            "variant: Data\ncases:\n  - name: text\n    tag: \"mod:text\"\n    type: string\n  - name: flag\n    tag: \"mod:flag\"\n    type: bool\n",
            "struct: Entry\nfields:\n  - kind: Id\n  - id: u8\n  - name: data\n    type: Data\n    tag: kind\n");
        Assert.Equal("0f6d696e6563726166743a626c6f636b00000007", Encode(project, "Source", """{"block": 7}"""));
        Assert.Equal("""{"none":null}""", Decode(project, "Source", "0e6d696e6563726166743a6e6f6e65")!.ToJsonString());
        Assert.Equal("""{"kind":"mod:flag","id":3,"data":{"flag":true}}""", Decode(project, "Entry", "086d6f643a666c61670301")!.ToJsonString());
        Assert.Equal("086d6f643a74657874" + "05" + "0161", Encode(project, "Entry", """{"kind": "mod:text", "id": 5, "data": {"text": "a"}}"""));
        Assert.False(new Codec(project).Encode("Entry", JsonNode.Parse("""{"kind": "mod:flag", "id": 5, "data": {"text": "a"}}""")).Success);
        Assert.Contains(new Codec(project).Decode("Source", Hex.Parse("0178")).Diagnostics, d => d.Message.Contains("no case with tag 'x'"));
    }

    [Fact]
    public void ArraysUntilAnElement()
    {
        var project = Project(Endian.Big,
            "struct: Item\nfields:\n  - index: u8\n  - name: value\n    type: i16\n    if: index != 255\n",
            "struct: Items\nfields:\n  - name: items\n    type: {array: Item, until: \"index == 255\"}\n  - after: u8\n");
        Assert.Equal("010007" + "02ffff" + "ff" + "09",
            Encode(project, "Items", """{"items": [{"index": 1, "value": 7}, {"index": 2, "value": -1}, {"index": 255}], "after": 9}"""));
        Assert.Equal("""{"items":[{"index":255}],"after":9}""", Decode(project, "Items", "ff09")!.ToJsonString());

        var codec = new Codec(project);
        Assert.Contains(codec.Encode("Items", JsonNode.Parse("""{"items": [{"index": 1, "value": 7}], "after": 9}""")).Diagnostics,
            d => d.Message.Contains("The last element must end the array"));
        Assert.Contains(codec.Encode("Items", JsonNode.Parse("""{"items": [{"index": 255}, {"index": 255}], "after": 9}""")).Diagnostics,
            d => d.Message.Contains("a reader stops after this element"));
        Assert.False(codec.Encode("Items", JsonNode.Parse("""{"items": [], "after": 9}""")).Success);
        Assert.Contains(codec.Decode("Items", Hex.Parse("010007")).Diagnostics, d => d.Message.Contains("The data ends before an element where index == 255"));

        var random = new RandomValues(project, seed: 3);
        for (var i = 0; i < 20; i++)
        {
            var result = Simulator.RoundTripValue(codec, Codec.ParseType("Items"), random.Generate(Codec.ParseType("Items")));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
    }

    [Fact]
    public void BitfieldMatchesTheJavaPositionExample()
    {
        var project = Project(Endian.Big, "bitfield: Position\ntype: i64\nfields:\n  - {name: x, bits: 26, signed: true}\n  - {name: z, bits: 26, signed: true}\n  - {name: y, bits: 12, signed: true}\n");
        Assert.Equal("4607632c15b4833f", Encode(project, "Position", """{"x": 18357644, "z": -20882616, "y": 831}"""));
        Assert.Equal("""{"x":-1,"z":0,"y":-2048}""", Decode(project, "Position", "ffffffc000000800")!.ToJsonString());
    }

    [Fact]
    public void SizedSectionsAndEof()
    {
        var project = Project(Endian.Little,
            "struct: Inner\nfields:\n  - a: u8\n  - rest: bytes[eof]\n",
            "struct: Outer\nfields:\n  - name: inner\n    type: {sized: Inner, length: u8}\n  - after: u8\n");
        Assert.Equal("03" + "01aabb" + "09", Encode(project, "Outer", """{"inner": {"a": 1, "rest": "aabb"}, "after": 9}"""));
        Assert.Equal("aabb", Decode(project, "Outer", "0301aabb09")!["inner"]!["rest"]!.GetValue<string>());
    }

    [Fact]
    public void FixedPointAndMaps()
    {
        var codec = new Codec(Project(Endian.Big));
        var fixedPoint = DslReader.ReadType("{fixed: i32, fraction: 5}");
        var encoded = codec.Encode(fixedPoint, JsonValue.Create(1.5));
        Assert.Equal("00000030", Hex.Format(encoded.Bytes));
        Assert.Equal(1.5, codec.Decode(fixedPoint, encoded.Bytes).Value!.GetValue<double>());

        var map = DslReader.ReadType("{map: {key: string, value: u8}, length: u8}");
        var mapBytes = codec.Encode(map, JsonNode.Parse("""{"a": 1, "b": 2}"""));
        Assert.Equal("02" + "0161" + "01" + "0162" + "02", Hex.Format(mapBytes.Bytes));
        Assert.Equal("""{"a":1,"b":2}""", codec.Decode(map, mapBytes.Bytes).Value!.ToJsonString());

        var numberKeys = DslReader.ReadType("{map: {key: u8, value: bool}}");
        Assert.Equal("""[{"key":7,"value":true}]""", codec.Decode(numberKeys, Hex.Parse("010701")).Value!.ToJsonString());
    }

    [Theory]
    [InlineData(NbtFlavor.Java)]
    [InlineData(NbtFlavor.JavaNetwork)]
    [InlineData(NbtFlavor.Bedrock)]
    [InlineData(NbtFlavor.BedrockNetwork)]
    public void NbtRoundTrips(NbtFlavor flavor)
    {
        const string snbt = "{byte:1b,short:-2s,int:300,long:5000000000L,float:1.5f,double:-0.25d,text:\"a \\\"q\\\"\",bytes:[B;1b,-1b]," +
                            "ints:[I;1,-1],longs:[L;2L],list:[{a:1},{a:2}],empty:[],nested:{deep:{x:\"y\"}}}";
        var tag = Nbt.ParseSnbt(snbt);
        Assert.Equal(snbt, Nbt.ToSnbt(tag));

        var writer = new WireWriter();
        Nbt.Write(writer, flavor, flavor == NbtFlavor.JavaNetwork ? "" : "root", tag);
        var read = Nbt.Read(new WireReader(writer.ToArray()), flavor);
        Assert.NotNull(read);
        Assert.Equal(flavor == NbtFlavor.JavaNetwork ? "" : "root", read.Value.Name);
        Assert.Equal(snbt, Nbt.ToSnbt(read.Value.Tag));
    }

    [Fact]
    public void NbtNetworkFlavorUsesVarints()
    {
        var writer = new WireWriter();
        Nbt.Write(writer, NbtFlavor.BedrockNetwork, "", Nbt.ParseSnbt("{a:-1}"));
        // compound, name "", int "a" = zigzag(-1), end
        Assert.Equal("0a00" + "030161" + "01" + "00", Hex.Format(writer.Written));
    }

    [Fact]
    public void ExpressionsEvaluate()
    {
        var evaluator = new ExprEvaluator((e, m) => e == "Mode" && m == "b" ? 2 : null);
        var scope = new Scope(new() { ["x"] = 6m, ["flag"] = true, ["items"] = new List<object?> { 1m, 2m, 3m }, ["s"] = "abc" });
        Assert.Equal(13m, evaluator.Evaluate(Expr.Parse("x * 2 + 1"), scope));
        Assert.Equal(true, evaluator.Evaluate(Expr.Parse("flag && x > 5 || false"), scope));
        Assert.Equal(2m, evaluator.Evaluate(Expr.Parse("x & 3"), scope));
        Assert.Equal(24m, evaluator.Evaluate(Expr.Parse("x << 2"), scope));
        Assert.Equal(3m, evaluator.Evaluate(Expr.Parse("len(items)"), scope));
        Assert.Equal(2m, evaluator.Evaluate(Expr.Parse("items[1]"), scope));
        Assert.Equal(true, evaluator.Evaluate(Expr.Parse("Mode::b == 2"), scope));
        Assert.Equal("yes", evaluator.Evaluate(Expr.Parse("x != 6 ? 'no' : \"yes\""), scope));
        Assert.Equal(3m, evaluator.Evaluate(Expr.Parse("len(s)"), scope));
        Assert.Equal(-7m, evaluator.Evaluate(Expr.Parse("~x"), scope));
        Assert.Throws<ExprException>(() => Expr.Parse("x +"));
        Assert.Throws<ExprException>(() => evaluator.Evaluate(Expr.Parse("unknown"), scope));
        Assert.Equal(["x", "items"], Expr.Parse("x + len(items) + Mode::b").Names());
    }

    private sealed class Scope(Dictionary<string, object?> values) : IExprScope
    {
        public bool TryGet(string name, out object? value) => values.TryGetValue(name, out value);
    }
}
