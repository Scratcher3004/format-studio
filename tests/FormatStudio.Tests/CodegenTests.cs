using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>
/// Compiles the generated C# against FormatStudio.Runtime and checks it against the simulator: bytes encoded by the
/// simulator from random values must be read and written back unchanged by the generated code.
/// </summary>
public class CodegenTests
{
    private static readonly CodegenTarget Target = new()
    {
        Name = "csharp",
        Generator = "csharp",
        Output = "generated",
        Options = new JsonObject { ["namespace"] = "Generated.Test", ["registry"] = "TestPackets" },
    };

    [Fact]
    public void JavaSampleMatchesTheSimulator()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var failures = GeneratedCodeVerifier.CheckAgainstSimulator(project, Target, typeof(FormatReader).Assembly, iterations: 25);
        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(20)));
    }

    [Theory]
    [InlineData("java")]
    [InlineData("java-network")]
    [InlineData("bedrock")]
    [InlineData("bedrock-network")]
    public void NbtFlavorsMatchTheSimulator(string flavor)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), $"name: p\ndefaults:\n  nbt: {flavor}\nsources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"), "struct: A\nfields:\n  - before: u8\n  - tag: nbt\n  - after: u8\n");
        var project = FormatProject.Load(directory);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var failures = GeneratedCodeVerifier.CheckAgainstSimulator(project, Target, typeof(FormatReader).Assembly, iterations: 40);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void UntilArraysAndStringTagsMatchTheSimulator()
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\ndefaults:\n  endian: big\n  length: varint32\nsources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "Item.yaml"), "struct: Item\nfields:\n  - more: bool\n  - value: i16\n");
        File.WriteAllText(Path.Combine(directory, "types", "Source.yaml"),
            "variant: Source\ntag: string\ncases:\n  - {name: block, tag: \"minecraft:block\", type: i32}\n  - {name: none, tag: \"minecraft:none\"}\n");
        File.WriteAllText(Path.Combine(directory, "types", "Data.yaml"),
            "variant: Data\ncases:\n  - {name: text, tag: \"mod:text\", type: string}\n  - {name: flag, tag: \"mod:flag\", type: bool}\n");
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"),
            "struct: A\nfields:\n  - name: items\n    type: {array: Item, until: \"!more\"}\n  - source: Source\n  - kind: string\n" +
            "  - name: data\n    type: Data\n    tag: kind\n  - after: u8\n");
        var project = FormatProject.Load(directory);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var failures = GeneratedCodeVerifier.CheckAgainstSimulator(project, Target, typeof(FormatReader).Assembly, iterations: 40);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void GeneratedDispatchCallsTheVisitor()
    {
        var project = FormatProject.Load(TestPaths.JavaSample);
        foreach (var packet in project.Packets.Where(p => p.Groups.Contains("play")).ToList()) project.Remove(packet.Name);
        var output = CodeGeneration.Generate(project, Target);
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
                return global::Generated.Test.TestPackets.Dispatch(id, ref reader, ref visitor) ? visitor.Name : null;
              }
            }
            """, typeof(FormatReader).Assembly);
        var run = assembly.GetType("Harness")!.GetMethod("Run")!;
        Assert.Equal("StatusPing:1", run.Invoke(null, [1u, new byte[8]]));
        Assert.Null(run.Invoke(null, [99u, Array.Empty<byte>()]));
    }

    [Fact]
    public void NbtWithoutFlavorIsAnError()
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\nsources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"), "struct: A\nfields:\n  - tag: nbt\n  - other: {nbt: java}\n");
        var project = FormatProject.Load(directory);
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
