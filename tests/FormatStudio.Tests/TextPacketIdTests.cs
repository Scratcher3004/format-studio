using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Packets with text ids (<c>id: "mod:move"</c>): the DSL, validation, and registries that dispatch strings.</summary>
public class TextPacketIdTests
{
    private static FormatProject Project(string settings, params (string File, string Text)[] types)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        foreach (var (file, text) in types) File.WriteAllText(Path.Combine(directory, "types", file), text);
        return FormatProject.Load(directory);
    }

    private static CodegenTarget Target(string generator)
    {
        var options = generator.StartsWith("java", StringComparison.Ordinal)
            ? new JsonObject { ["package"] = "generated.test" }
            : new JsonObject { ["namespace"] = "Generated.Test" };
        return new CodegenTarget { Name = generator, Generator = generator, Output = "generated", Options = options };
    }

    private static readonly (string, string)[] Channels =
    [
        ("Move.yaml", """
            packet: Move
            id: "mod:move"
            fields:
              - x: i32
              - y: i32
            """),
        ("Hello.yaml", """
            packet: Hello
            id: mod:hello
            fields:
              - {name: name, type: string}
            """),
        ("Quoted.yaml", """
            packet: Quoted
            id: "39"
            fields:
              - flag: bool
            """),
    ];

    private static FormatProject ChannelProject() => Project("name: p\ndefaults: {endian: big}\n", Channels);

    [Fact]
    public void QuotedOrNonNumericIdsAreText()
    {
        var project = ChannelProject();
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var move = (StructDef)project.Find("Move")!;
        Assert.Equal("mod:move", move.TextId);
        Assert.Null(move.Id);
        Assert.Equal("mod:hello", ((StructDef)project.Find("Hello")!).TextId);
        Assert.Equal("39", ((StructDef)project.Find("Quoted")!).TextId);

        // Written back, the text stays text: "39" keeps its quotes.
        var written = DslWriter.Write(project.Find("Quoted")!);
        Assert.Contains("id: \"39\"", written);
        var read = (StructDef)DslReader.Read(written, "Quoted.yaml", new DiagnosticBag())!;
        Assert.Equal("39", read.TextId);
        Assert.Contains("id: mod:move", DslWriter.Write(move));

        var numeric = (StructDef)DslReader.Read("packet: N\nid: 0x27\nfields: []\n", "N.yaml", new DiagnosticBag())!;
        Assert.Equal(0x27, numeric.Id);
        Assert.Null(numeric.TextId);
    }

    [Fact]
    public void TextIdsAreCheckedLikeIntegerIds()
    {
        var mixed = Project("name: p\n", [.. Channels,
            ("Ping.yaml", "packet: Ping\nid: 1\nfields: []\n"),
            ("Again.yaml", "packet: Again\nid: mod:move\nfields: []\n")]);
        var errors = mixed.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        Assert.Contains("Packets Again, Move share id mod:move", errors);
        Assert.Contains(errors, e => e.StartsWith("The packets mix integer and text ids", StringComparison.Ordinal));

        // With protocols, each registry has one kind of ids.
        var split = Project("""
            name: p
            protocols:
              - {name: Channels, groups: [custom]}
              - {name: Play, groups: [play]}
            """,
            ("Move.yaml", "packet: Move\nid: mod:move\ngroups: [custom]\nfields: []\n"),
            ("Ping.yaml", "packet: Ping\nid: 1\ngroups: [play]\nfields: []\n"));
        Assert.False(split.Diagnostics.HasErrors, string.Join("\n", split.Diagnostics));
    }

    [Fact]
    public void PacketHeadersCantHoldTextIds()
    {
        var project = ChannelProject();
        var encoded = Simulator.EncodePacket(new Codec(project), (StructDef)project.Find("Move")!, JsonNode.Parse("""{"x": 1, "y": 2}"""),
            new PacketHeader(new PrimitiveRef(PrimitiveKind.VarInt32), ulong.MaxValue));
        Assert.Contains(encoded.Diagnostics, d => d.Message.Contains("Move has a text id, which a packet header can't hold"));
    }

    [Theory]
    [MemberData(nameof(CodegenTests.Generators), MemberType = typeof(CodegenTests))]
    public void TextIdPacketsMatchTheSimulator(string generator)
    {
        CodegenTests.AssertMatchesSimulator(ChannelProject(), generator, 10);
    }

    [Fact]
    public void GeneratedCSharpDispatchesTextIds()
    {
        var output = CodeGeneration.Generate(ChannelProject(), Target("csharp"));
        Assert.Empty(output.Diagnostics.Where(d => d.Severity == Severity.Error));
        Assert.Contains("public const string Id = \"mod:move\";", output.FileOf("Move")!.Content);
        var assembly = GeneratedCodeVerifier.Compile(output, """
            using FormatStudio.Runtime;
            public struct Names : IFormatTextPacketVisitor {
              public string Seen;
              public void Visit<T>(ref T packet) where T : IFormatTextPacket<T> => Seen = T.PacketId;
            }
            public static class Harness {
              public static string Dispatch(string id) {
                var reader = new FormatReader(new byte[] { 0, 0, 0, 1, 0, 0, 0, 2 });
                var visitor = new Names();
                return global::Generated.Test.Packets.Dispatch(id, ref reader, ref visitor) ? visitor.Seen : "unknown";
              }
              public static string Ids() => string.Join(",", global::Generated.Test.Packets.Ids) + ";" + global::Generated.Test.Packets.NameOf("mod:hello");
            }
            """, typeof(FormatReader).Assembly);
        var harness = assembly.GetType("Harness")!;
        Assert.Equal("mod:move", harness.GetMethod("Dispatch")!.Invoke(null, ["mod:move"]));
        Assert.Equal("unknown", harness.GetMethod("Dispatch")!.Invoke(null, ["mod:other"]));
        Assert.Equal("39,mod:hello,mod:move;Hello", harness.GetMethod("Ids")!.Invoke(null, []));
    }

    [Fact]
    public void GeneratedJavaDispatchesTextIds()
    {
        var java = JavaToolchain.Find();
        if (java == null) Assert.Skip("No JDK: set JAVA_HOME or FORMAT_STUDIO_JDK, or put javac on the PATH");
        var output = CodeGeneration.Generate(ChannelProject(), Target("java-stream"));
        Assert.Empty(output.Diagnostics.Where(d => d.Severity == Severity.Error));
        var classes = java.Compile(output, """
            import formatstudio.runtime.*;
            import generated.test.*;

            public final class Harness {
                public static void main(String[] args) throws Exception {
                    byte[] bytes = {0, 0, 0, 1, 0, 0, 0, 2};
                    FormatTextPacket packet = Packets.read("mod:move", new FormatReader(bytes));
                    System.out.println(packet.packetId() + " " + ((Move) packet).y);
                    System.out.println(Packets.read("mod:other", new FormatReader(bytes)));
                    System.out.println(String.join(",", Packets.ids()) + ";" + Packets.nameOf("mod:hello") + ";" + Move.ID);
                }
            }
            """, TestPaths.NewTempDirectory());
        Assert.Equal("mod:move 2\nnull\n39,mod:hello,mod:move;Hello;mod:move\n", java.Run(classes, "Harness", ""));
    }
}
