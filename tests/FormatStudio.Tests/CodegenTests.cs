using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>
/// Compiles the generated code (C# against FormatStudio.Runtime, Java with a JDK) and checks it against the simulator:
/// bytes encoded by the simulator from random values must be read and written back unchanged by the generated code.
/// The Java tests are skipped without a JDK (JAVA_HOME, FORMAT_STUDIO_JDK or javac on the PATH).
/// </summary>
public class CodegenTests
{
    public static TheoryData<string> Generators => ["csharp", "csharp-stream", "java-stream"];

    /// <summary>Java options besides the defaults: unsigned handling and collections.</summary>
    public static TheoryData<string, string> JavaVariants => new()
    {
        { "signed", "list" },
        { "widen", "array" },
        { "signed", "array" },
    };

    private static CodegenTarget Target(string generator, JsonObject? extra = null)
    {
        var options = generator.StartsWith("java", StringComparison.Ordinal)
            ? new JsonObject { ["package"] = "generated.test", ["registry"] = "TestPackets" }
            : new JsonObject { ["namespace"] = "Generated.Test", ["registry"] = "TestPackets" };
        foreach (var (key, value) in extra ?? []) options[key] = value?.DeepClone();
        return new CodegenTarget { Name = generator, Generator = generator, Output = "generated", Options = options };
    }

    private static JsonObject JavaOptions(string unsigned, string collections) => new() { ["unsigned"] = unsigned, ["collections"] = collections };

    private static JavaToolchain Java()
    {
        var java = JavaToolchain.Find();
        if (java == null) Assert.Skip("No JDK: set JAVA_HOME or FORMAT_STUDIO_JDK, or put javac on the PATH");
        return java;
    }

    internal static void AssertMatchesSimulator(FormatProject project, string generator, int iterations, JsonObject? options = null)
    {
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var failures = generator.StartsWith("java", StringComparison.Ordinal)
            ? GeneratedCodeVerifier.CheckJavaAgainstSimulator(project, Target(generator, options), Java(), iterations)
            : GeneratedCodeVerifier.CheckAgainstSimulator(project, Target(generator, options), typeof(FormatReader).Assembly, iterations);
        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(20)));
    }

    private static FormatProject Project(string settings, params (string File, string Text)[] types)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        foreach (var (file, text) in types) File.WriteAllText(Path.Combine(directory, "types", file), text);
        return FormatProject.Load(directory);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void JavaSampleMatchesTheSimulator(string generator)
    {
        AssertMatchesSimulator(FormatProject.Load(TestPaths.JavaSample), generator, 25);
    }

    [Theory]
    [InlineData("java", "csharp")]
    [InlineData("java-network", "csharp")]
    [InlineData("bedrock", "csharp")]
    [InlineData("bedrock-network", "csharp")]
    [InlineData("java", "csharp-stream")]
    [InlineData("bedrock-network", "csharp-stream")]
    [InlineData("java", "java-stream")]
    [InlineData("java-network", "java-stream")]
    [InlineData("bedrock", "java-stream")]
    [InlineData("bedrock-network", "java-stream")]
    public void NbtFlavorsMatchTheSimulator(string flavor, string generator)
    {
        var project = Project($"name: p\ndefaults:\n  nbt: {flavor}\nsources: [types/*.yaml]\n",
            ("A.yaml", "struct: A\nfields:\n  - before: u8\n  - tag: nbt\n  - after: u8\n"));
        AssertMatchesSimulator(project, generator, 40);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void UntilArraysAndStringTagsMatchTheSimulator(string generator)
    {
        var project = Project("name: p\ndefaults:\n  endian: big\n  length: varint32\nsources: [types/*.yaml]\n",
            ("Item.yaml", "struct: Item\nfields:\n  - more: bool\n  - value: i16\n"),
            ("Source.yaml", "variant: Source\ntag: string\ncases:\n  - {name: block, tag: \"minecraft:block\", type: i32}\n  - {name: none, tag: \"minecraft:none\"}\n"),
            ("Data.yaml", "variant: Data\ncases:\n  - {name: text, tag: \"mod:text\", type: string}\n  - {name: flag, tag: \"mod:flag\", type: bool}\n"),
            ("A.yaml", "struct: A\nfields:\n  - name: items\n    type: {array: Item, until: \"!more\"}\n  - source: Source\n  - kind: string\n" +
                       "  - name: data\n    type: Data\n    tag: kind\n  - after: u8\n"));
        AssertMatchesSimulator(project, generator, 40);
    }

    /// <summary>Most of the DSL in one project: every primitive, enums of all sorts, bit fields, variants, collections, expressions.</summary>
    private static FormatProject KitchenSink() => Project("name: p\ndefaults:\n  endian: little\n  length: varuint32\nsources: [types/*.yaml]\n",
        ("Color.yaml", "enum: Color\ntype: u8\nvalues:\n  red: 0\n  green: 1\n  value: 2\n"),
        ("Perms.yaml", "enum: Perms\ntype: u16\nflags: true\nvalues:\n  read: 1\n  write: 2\n  exec: 4\n"),
        ("Mode.yaml", "enum: Mode\ntype: zigzag32\nallowUnknown: true\nvalues:\n  idle: 0\n  busy: 1\n"),
        ("Packed.yaml", "bitfield: Packed\ntype: u32\nfields:\n  - {name: enabled, bits: 1, type: bool}\n  - {name: color, bits: 2, type: Color}\n" +
                        "  - {name: delta, bits: 5, signed: true}\n  - {name: mode, bits: 2, type: Mode}\n  - {name: rest, bits: 22}\n"),
        ("Shape.yaml", "variant: Shape\ntag: {type: Color, as: varuint32}\ncases:\n  - {name: none, tag: red}\n  - {name: circle, tag: green, type: f32}\n" +
                       "  - {name: label, tag: value, type: string}\n"),
        ("Event.yaml", "variant: Event\ntag: u8\ncases:\n  - {name: ping, tag: 1, type: i64}\n  - {name: tree, tag: 2, type: Tree}\n  - {name: stop, tag: 3}\n"),
        ("Tree.yaml", "struct: Tree\nfields:\n  - value: i16\n  - name: children\n    type: Tree[]\n    maxItems: 3\n"),
        ("Entry.yaml", "struct: Entry\nfields:\n  - id: uuid\n  - idBe: uuidbe\n  - scores: {map: {key: string, value: i32be}}\n  - maybe: u32?\n" +
                       "  - maybeText: {optional: string}\n  - pos: {fixed: i32, fraction: 5}\n  - inner: {sized: Tree, length: u16}\n" +
                       "  - nested: u8[][]\n  - perms: Perms\n  - mode: Mode\n  - wide: {type: Color, as: varint32}\n  - big: u64\n  - small: i8\n" +
                       "  - half: u16be\n  - word: varuint64\n  - zz: zigzag64\n  - z32: zigzag32\n  - ratio: f64\n  - packed: Packed\n  - shape: Shape\n" +
                       "  - event: Event\n  - signed: i32\n  - unsigned: u32\n  - vu: varuint32\n  - vi: varint64\n  - fixedBytes: bytes[4]\n" +
                       "  - fixedItems: i16[2]\n  - prefixed: u8[u16]\n"),
        ("Header.yaml", "struct: Header\nfields:\n  - {name: magic, type: u16, const: 0xCAFE}\n  - {name: version, type: string, const: v1}\n" +
                        "  - {name: count, type: varint32, computed: len(items)}\n  - {name: hasNote, type: bool, computed: note != null}\n" +
                        "  - kind: Color\n  - {name: items, type: \"i32[count]\"}\n  - {name: note, type: string, if: hasNote}\n" +
                        "  - {name: extra, type: u8, if: \"kind == Color::green\"}\n  - {name: label, type: {string: utf8, length: zero}}\n" +
                        "  - {name: textLen, type: u8, computed: \"len(scores) * 2\"}\n  - {name: scores, type: \"u16[textLen / 2]\"}\n" +
                        "  - {name: tagNum, type: u8, computed: tagof(shape)}\n  - shape: Shape\n  - tail: bytes[eof]\n"),
        ("Rest.yaml", "struct: Rest\nfields:\n  - head: u8\n  - items: i16[eof]\n"),
        ("Ping.yaml", "packet: Ping\nid: 5\nfields:\n  - time: i64\n"));

    [Theory]
    [MemberData(nameof(Generators))]
    public void KitchenSinkMatchesTheSimulator(string generator)
    {
        AssertMatchesSimulator(KitchenSink(), generator, 30);
    }

    [Theory]
    [MemberData(nameof(JavaVariants))]
    public void JavaOptionsMatchTheSimulator(string unsigned, string collections)
    {
        AssertMatchesSimulator(KitchenSink(), "java-stream", 30, JavaOptions(unsigned, collections));
        AssertMatchesSimulator(FormatProject.Load(TestPaths.JavaSample), "java-stream", 10, JavaOptions(unsigned, collections));
    }

    [Fact]
    public void JavaTypesFollowTheUnsignedOption()
    {
        var project = KitchenSink();
        string Code(string unsigned, string collections, string type) =>
            CodeGeneration.Generate(project, Target("java-stream", JavaOptions(unsigned, collections))).FileOf(type)!.Content;

        var widened = Code("widen", "list", "Entry");
        Assert.Contains("public long unsigned;", widened);
        Assert.Contains("public int half;", widened);
        Assert.Contains("public java.util.List<java.util.List<Integer>> nested;", widened);
        Assert.Contains("public int perms;", widened);
        var signed = Code("signed", "array", "Entry");
        Assert.Contains("public int unsigned;", signed);
        Assert.Contains("public short half;", signed);
        Assert.Contains("public byte[][] nested;", signed);
        Assert.Contains("public short perms;", signed);
        Assert.Contains("public int mode;", signed);

        // Enums use their own type: u8 → int (widen) or byte (signed), zigzag32 → int.
        Assert.Contains("public final int value;", Code("widen", "list", "Color"));
        Assert.Contains("public final byte value;", Code("signed", "list", "Color"));
        Assert.Contains("public static final int busy = 1;", Code("widen", "list", "Mode"));
        Assert.Contains("public static final short exec = (short) 4L;", Code("signed", "list", "Perms"));

        Assert.Contains("doesn't fit a int", Code("checked", "list", "Entry"));
        Assert.DoesNotContain("doesn't fit", Code("signed", "list", "Entry"));
    }

    [Fact]
    public void WidenedU64IsAWarning()
    {
        var project = KitchenSink();
        var widened = CodeGeneration.Generate(project, Target("java-stream"));
        var warnings = widened.Diagnostics.Where(d => d.Severity == Severity.Warning && d.Message.Contains("64-bit unsigned")).Select(d => d.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, m => m.StartsWith("Entry.big:", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.StartsWith("Entry.word:", StringComparison.Ordinal));
        var signed = CodeGeneration.Generate(project, Target("java-stream", JavaOptions("signed", "list")));
        Assert.DoesNotContain(signed.Diagnostics, d => d.Message.Contains("64-bit unsigned"));
    }

    [Fact]
    public void CheckedUnsignedValuesRejectLargeValues()
    {
        var java = Java();
        var project = Project("name: p\nsources: [types/*.yaml]\n",
            ("Small.yaml", "struct: Small\nfields:\n  - a: u8\n  - b: u32be\n  - c: varuint64\n"));
        var output = CodeGeneration.Generate(project, Target("java-stream", JavaOptions("checked", "list")));
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        var classes = java.Compile(output, """
            import formatstudio.runtime.*;
            import generated.test.*;

            public final class Harness {
                static String read(byte[] data) {
                    try {
                        Small small = Small.read(new FormatReader(data));
                        return small.a + "," + small.b + "," + small.c;
                    } catch (Exception e) {
                        return e.getMessage();
                    }
                }

                public static void main(String[] args) throws Exception {
                    System.out.println(read(new byte[] {127, 0x7F, -1, -1, -1, 5}));
                    System.out.println(read(new byte[] {-128, 0, 0, 0, 0, 0}));
                    System.out.println(read(new byte[] {1, -128, 0, 0, 0, 0}));
                    Small small = new Small();
                    small.a = -1;
                    try {
                        small.write(new FormatWriter(new java.io.ByteArrayOutputStream()));
                    } catch (FormatDataException e) {
                        System.out.println(e.getMessage());
                    }
                }
            }
            """, TestPaths.NewTempDirectory());
        Assert.Equal("127,2147483647,5\na doesn't fit a byte\nb doesn't fit a int\na must not be negative (it is unsigned)\n",
            java.Run(classes, "Harness", ""));
    }

    [Fact]
    public void SkippedDefinitionsAreReferencedButNotGenerated()
    {
        var project = Project("name: p\nsources: [types/*.yaml]\ncodegen:\n  - {name: main, generator: csharp, output: out}\n  - {name: other, generator: csharp, output: out2}\n",
            ("Hand.yaml", "struct: Hand\nskipCodegen: true\nfields:\n  - a: u8\n"),
            ("JavaOnly.yaml", "struct: JavaOnly\nskipCodegen: [java-stream, other]\nfields:\n  - a: u8\n"),
            ("User.yaml", "struct: User\nfields:\n  - hand: Hand\n  - j: JavaOnly\n"));
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        Assert.Equal(CodegenSkip.Everywhere, project.Types["Hand"].SkipCodegen);

        CodegenTarget Named(string name) => project.Settings.Codegen.Single(t => t.Name == name);
        var main = CodeGeneration.Generate(project, Named("main"));
        Assert.Null(main.FileOf("Hand"));
        Assert.NotNull(main.FileOf("JavaOnly"));
        Assert.Contains("Hand.Read(", main.FileOf("User")!.Content);
        Assert.Null(CodeGeneration.Generate(project, Named("other")).FileOf("JavaOnly"));
        Assert.Null(CodeGeneration.Generate(project, Target("java-stream")).FileOf("JavaOnly"));
        Assert.Equal(["Hand"], CodeGeneration.Skipped(project, Named("main")));
        Assert.Equal(["Hand", "JavaOnly"], CodeGeneration.Skipped(project, Named("other")).Order());
    }

    [Fact]
    public void SkipCodegenRoundTripsAndChecksNames()
    {
        foreach (var yaml in new[] { "skipCodegen: true\n", "skipCodegen:\n  - java-stream\n  - main\n" })
        {
            var text = "struct: S\n" + yaml + "fields:\n  - a: u8\n";
            var definition = FormatStudio.Dsl.DslReader.Read(text, "S.yaml", new DiagnosticBag());
            Assert.Equal(text, FormatStudio.Dsl.DslWriter.Write(definition!).Replace("\r\n", "\n"));
        }

        var project = Project("name: p\nsources: [types/*.yaml]\n", ("S.yaml", "struct: S\nskipCodegen: [nope]\nfields:\n  - a: u8\n"));
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("'nope'"));
        var old = CodeGeneration.Generate(project, Target("csharp", new JsonObject { ["exclude"] = new JsonArray("S") }));
        Assert.Contains(old.Diagnostics, d => d.Message.Contains("replaced by 'skipCodegen'"));
    }

    [Theory]
    [InlineData("struct: S\n# keep\nfields:\n  - a: u8\n", "struct: S\nskipCodegen:\n  - cs\n# keep\nfields:\n  - a: u8\n")]
    [InlineData("packet: P\nid: 1\ndoc: |\n  text\n\nfields: []\n", "packet: P\nid: 1\ndoc: |\n  text\nskipCodegen:\n  - cs\n\nfields: []\n")]
    [InlineData("struct: S\nskipCodegen: true # x\nfields: []\n", "struct: S\nskipCodegen:\n  - cs\nfields: []\n")]
    [InlineData("struct: S\nskipCodegen:\n- a\n- b\nfields: []\n", "struct: S\nskipCodegen:\n  - cs\nfields: []\n")]
    public void SetSkipCodegenKeepsTheRestOfTheText(string text, string expected)
    {
        var skip = new CodegenSkip(false, ["cs"]);
        Assert.Equal(expected, FormatStudio.Dsl.DslWriter.SetSkipCodegen(text, skip));
        Assert.Equal(expected.Replace("\n", "\r\n"), FormatStudio.Dsl.DslWriter.SetSkipCodegen(text.Replace("\n", "\r\n"), skip));
        var removed = FormatStudio.Dsl.DslWriter.SetSkipCodegen(expected, null);
        Assert.Null(FormatStudio.Dsl.DslReader.Read(removed, "S.yaml", new DiagnosticBag())!.SkipCodegen);
        Assert.Equal(CodegenSkip.Everywhere, FormatStudio.Dsl.DslReader.Read(FormatStudio.Dsl.DslWriter.SetSkipCodegen(text, CodegenSkip.Everywhere), "S.yaml", new DiagnosticBag())!.SkipCodegen);
    }

    [Fact]
    public void CheckedUnsignedRejectsMembersAboveTheSignedRange()
    {
        var project = Project("name: p\nsources: [types/*.yaml]\n", ("Big.yaml", "enum: Big\ntype: u8\nvalues:\n  low: 1\n  high: 200\n"));
        var output = CodeGeneration.Generate(project, Target("java-stream", JavaOptions("checked", "list")));
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Big.high = 200 doesn't fit a byte"));
        Assert.Contains("high((byte) 200L)", CodeGeneration.Generate(project, Target("java-stream", JavaOptions("signed", "list"))).FileOf("Big")!.Content);
    }

    [Fact]
    public void ChoiceOptionsAreChecked()
    {
        var target = Target("java-stream", new JsonObject { ["unsigned"] = "bogus" });
        var output = CodeGeneration.Generate(FormatProject.InMemory([]), target);
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("unsigned"));
    }

    [Fact]
    public void GeneratedDispatchCallsTheVisitor()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        var output = CodeGeneration.Generate(project, Target("csharp"));
        var assembly = GeneratedCodeVerifier.Compile(output, """
            using FormatStudio.Runtime;
            public struct NameVisitor : IFormatPacketVisitor {
              public string? Name;
              public void Visit<T>(ref T packet) where T : IFormatPacket<T> => Name = typeof(T).Name + ":" + T.PacketId;
            }
            public static class Harness {
              public static string? Run(uint id, byte[] data) {
                var reader = new FormatReader(data);
                var visitor = new NameVisitor();
                return global::Generated.Test.TestPackets.StatusServerbound.Dispatch(id, ref reader, ref visitor) ? visitor.Name : null;
              }
            }
            """, typeof(FormatReader).Assembly);
        var run = assembly.GetType("Harness")!.GetMethod("Run")!;
        Assert.Equal("StatusPing:1", run.Invoke(null, [1u, new byte[8]]));
        Assert.Null(run.Invoke(null, [99u, Array.Empty<byte>()]));
    }

    [Fact]
    public void GeneratedStreamDispatchCallsTheVisitor()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        var output = CodeGeneration.Generate(project, Target("csharp-stream"));
        var assembly = GeneratedCodeVerifier.Compile(output, """
            using FormatStudio.Runtime;
            public struct NameVisitor : IFormatStreamPacketVisitor {
              public string? Name;
              public void Visit<T>(ref T packet) where T : IFormatStreamPacket<T> => Name = typeof(T).Name + ":" + T.PacketId;
            }
            public static class Harness {
              public static string? Run(uint id, byte[] data) {
                var reader = new FormatStreamReader(new global::System.IO.MemoryStream(data));
                var visitor = new NameVisitor();
                return global::Generated.Test.TestPackets.StatusServerbound.Dispatch(id, reader, ref visitor) ? visitor.Name : null;
              }
            }
            """, typeof(FormatReader).Assembly);
        var run = assembly.GetType("Harness")!.GetMethod("Run")!;
        Assert.Equal("StatusPing:1", run.Invoke(null, [1u, new byte[8]]));
        Assert.Null(run.Invoke(null, [99u, Array.Empty<byte>()]));
    }

    [Fact]
    public void GeneratedJavaRegistryReadsPackets()
    {
        var java = Java();
        var project = FormatProject.Load(TestPaths.JavaSample);
        var output = CodeGeneration.Generate(project, Target("java-stream"));
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        var directory = TestPaths.NewTempDirectory();
        var classes = java.Compile(output, """
            import formatstudio.runtime.*;
            import generated.test.*;

            public final class Harness {
                public static void main(String[] args) throws Exception {
                    FormatPacket packet = TestPackets.StatusServerbound.read(1, new FormatReader(new byte[8]));
                    System.out.println(packet.getClass().getSimpleName() + ":" + packet.packetId() + ":" + TestPackets.StatusServerbound.nameOf(1));
                    System.out.println(TestPackets.StatusServerbound.read(99, new FormatReader(new byte[0])) == null);
                    System.out.println(TestPackets.HandshakingServerbound.nameOf(0) + ":" + TestPackets.LoginServerbound.nameOf(0));
                }
            }
            """, directory);
        Assert.Equal("StatusPing:1:StatusPing\ntrue\nHandshake:LoginStart\n", java.Run(classes, "Harness", ""));
    }

    [Fact]
    public void ProtocolsHaveRegistriesOfTheirOwn()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        var output = CodeGeneration.Generate(project, Target("csharp"));
        Assert.DoesNotContain(output.Diagnostics, d => d.Message.Contains("share id"));
        var assembly = GeneratedCodeVerifier.Compile(output, """
            public static class Harness {
              public static string? Run(string protocol, uint id) => protocol switch {
                "HandshakingServerbound" => global::Generated.Test.TestPackets.HandshakingServerbound.NameOf(id),
                "LoginServerbound" => global::Generated.Test.TestPackets.LoginServerbound.NameOf(id),
                _ => global::Generated.Test.TestPackets.PlayClientbound.NameOf(id),
              };
            }
            """, typeof(FormatReader).Assembly);
        var run = assembly.GetType("Harness")!.GetMethod("Run")!;
        Assert.Equal("Handshake", run.Invoke(null, ["HandshakingServerbound", 0u]));
        Assert.Equal("LoginStart", run.Invoke(null, ["LoginServerbound", 0u]));
        Assert.Equal("Particle", run.Invoke(null, ["PlayClientbound", 0x27u]));
        Assert.Null(run.Invoke(null, ["PlayClientbound", 0u]));
    }

    [Fact]
    public void WithoutProtocolsOneRegistryDispatchesTheFirstOfSharedIds()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        project.Settings.Protocols.Clear();
        var output = CodeGeneration.Generate(project, Target("csharp"));
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("Handshake, LoginStart share id 0"));
        var registry = output.Files.Single(f => f.Path == "TestPackets.g.cs").Content;
        Assert.Contains("public static bool Dispatch<TVisitor>", registry);
        Assert.DoesNotContain("class StatusServerbound", registry);
    }

    [Fact]
    public void ClashingProtocolNamesAreReported()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        project.Settings.Protocols.Add(new ProtocolDef { Name = "Slot", Groups = ["play"] });
        project.Settings.Protocols.Add(new ProtocolDef { Name = "TestPackets", Groups = ["play"] });
        var output = CodeGeneration.Generate(project, Target("java-stream"));
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Error && d.Message.StartsWith("Protocol Slot:"));
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Error && d.Message.StartsWith("Protocol TestPackets:"));
        Assert.Contains("public static final class PlayClientbound", output.Files.Single(f => f.Path.EndsWith("TestPackets.java")).Content);
    }

    [Fact]
    public void EveryGeneratorDescribesItsOptions()
    {
        foreach (var generator in CodeGeneration.Generators)
        {
            Assert.NotEmpty(generator.Title);
            Assert.NotEmpty(generator.DefaultOutput);
            Assert.Equal(generator.Options.Count, generator.Options.Select(o => o.Key).Distinct().Count());
            var output = generator.Generate(FormatProject.InMemory([]), [], new HashSet<string>());
            Assert.False(output.Diagnostics.HasErrors);
        }
    }

    [Fact]
    public void UnknownOptionsAreReported()
    {
        var project = FormatProject.InMemory([]);
        var target = new CodegenTarget { Name = "t", Generator = "java-stream", Output = "out", Options = new JsonObject { ["namespace"] = "X" } };
        var output = CodeGeneration.Generate(project, target);
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("unknown option 'namespace'"));
    }

    [Fact]
    public void GeneratedFilesKnowTheirType()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        foreach (var generator in CodeGeneration.Generators)
        {
            var output = CodeGeneration.Generate(project, CodeGeneration.DefaultTarget(generator));
            Assert.NotNull(output.FileOf("Slot"));
            Assert.Contains("Slot", output.FileOf("Slot")!.Content);
            Assert.Null(output.FileOf("NoSuchType"));
            Assert.All(output.Files, f => Assert.StartsWith(generator.Marker, f.Content));
        }
    }

    [Theory]
    [InlineData("generated/csharp", true)]
    [InlineData("out", true)]
    [InlineData("a/../b", true)]
    [InlineData("../elsewhere", false)]
    [InlineData("a/../..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("/abs/path", false)]
    public void TargetsMustStayInsideTheProject(string output, bool valid)
    {
        var target = new CodegenTarget { Name = "t", Generator = "csharp", Output = output };
        if (valid) CodeGeneration.Check(target);
        else Assert.Throws<FormatDefinitionException>(() => CodeGeneration.Check(target));
    }

    [Fact]
    public void SavingTargetsOnlyChangesTheCodegenSection()
    {
        var directory = TestPaths.NewTempDirectory();
        var file = Path.Combine(directory, "p.fstudio.yaml");
        const string before = "# My project\r\nname: p\r\ndescription: >-\r\n  folded\r\n  text\r\ncodegen:\r\n- name: old\r\n  generator: csharp\r\n" +
                              "  output: out/old   # a comment\r\n\r\n# Where the types are\r\nsources: [types/*.yaml]\r\n";
        File.WriteAllText(file, before);
        var project = FormatProject.Load(directory);
        var java = new CodegenTarget { Name = "java", Generator = "java-stream", Output = "generated/java", Options = new JsonObject { ["package"] = "a.b" } };

        project.SaveCodegen([project.Settings.Codegen[0], java]);
        var text = File.ReadAllText(file);
        Assert.StartsWith("# My project\r\nname: p\r\ndescription: >-\r\n  folded\r\n  text\r\ncodegen:\r\n", text);
        Assert.EndsWith("\r\n\r\n# Where the types are\r\nsources: [types/*.yaml]\r\n", text);
        var reloaded = FormatProject.Load(directory);
        Assert.Equal(["old", "java"], reloaded.Settings.Codegen.Select(t => t.Name));
        Assert.Equal("a.b", reloaded.Settings.Codegen[1].Options["package"]!.ToString());
        Assert.Equal("folded text", reloaded.Settings.Description);

        reloaded.SaveCodegen([]);
        Assert.Equal("# My project\r\nname: p\r\ndescription: >-\r\n  folded\r\n  text\r\n\r\n# Where the types are\r\nsources: [types/*.yaml]\r\n", File.ReadAllText(file));
        FormatProject.Load(directory).SaveCodegen([java]);
        Assert.EndsWith("sources: [types/*.yaml]\r\ncodegen:\r\n  - name: java\r\n    generator: java-stream\r\n    output: generated/java\r\n    options:\r\n      package: a.b\r\n",
            File.ReadAllText(file));
        Assert.Equal("java", FormatProject.Load(directory).Settings.Codegen.Single().Name);
    }

    [Fact]
    public void SavingTargetsChecksThem()
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\n");
        var project = FormatProject.Load(directory);
        var target = new CodegenTarget { Name = "a", Generator = "csharp", Output = "out" };
        Assert.Throws<FormatDefinitionException>(() => project.SaveCodegen([target, target]));
        Assert.Throws<FormatDefinitionException>(() => project.SaveCodegen([new CodegenTarget { Name = "a", Generator = "cobol", Output = "out" }]));
        Assert.Throws<FormatDefinitionException>(() => project.SaveCodegen([new CodegenTarget { Name = "a", Generator = "csharp", Output = "../out" }]));
        Assert.Equal("name: p\n", File.ReadAllText(Path.Combine(directory, "p.fstudio.yaml")));
    }

    [Fact]
    public void NbtWithoutFlavorIsAnError()
    {
        var project = Project("name: p\nsources: [types/*.yaml]\n", ("A.yaml", "struct: A\nfields:\n  - tag: nbt\n  - other: {nbt: java}\n"));
        Assert.Single(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("NBT without a flavor"));
    }

    [Fact]
    public void WriteOnlyTouchesGeneratedFiles()
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\nsources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"), "struct: A\nfields:\n  - x: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "B.yaml"), "struct: B\nfields:\n  - x: u8\n");
        var project = FormatProject.Load(directory);
        var target = new CodegenTarget { Name = "cs", Generator = "csharp", Output = "out" };

        var first = CodeGeneration.Write(project, target, CodeGeneration.Generate(project, target));
        Assert.Equal(2, first.Written.Count);

        var handWritten = Path.Combine(directory, "out", "Types", "B.g.cs");
        File.WriteAllText(handWritten, "// written by hand\n");
        var manual = Path.Combine(directory, "out", "Notes.txt");
        File.WriteAllText(manual, "keep");
        project.Remove("A");
        var second = CodeGeneration.Write(project, target, CodeGeneration.Generate(project, target));
        Assert.Equal([handWritten], second.Refused);
        Assert.Single(second.Deleted);
        Assert.False(File.Exists(Path.Combine(directory, "out", "Types", "A.g.cs")));
        Assert.Equal("// written by hand\n", File.ReadAllText(handWritten));
        Assert.True(File.Exists(manual));

        var outside = new CodegenTarget { Name = "cs", Generator = "csharp", Output = "../elsewhere" };
        Assert.Throws<FormatDefinitionException>(() => CodeGeneration.Write(project, outside, CodeGeneration.Generate(project, outside)));
    }
}
