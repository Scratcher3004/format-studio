using System.Text;
using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>
/// The netty generator of the minecraft library: records with Minecraft StreamCodecs. The output is compiled against
/// stand-ins of Minecraft's classes (<see cref="MinecraftStubs"/>) and the real netty-buffer, and every type's CODEC must
/// read and write back the bytes the simulator encodes from random values.
/// </summary>
public class NettyGeneratorTests
{
    private static CodegenTarget Target(string strategy = "auto") => new()
    {
        Name = "netty", Generator = "netty", Output = "generated",
        Options = new JsonObject { ["package"] = "generated.test", ["strategy"] = strategy },
    };

    private static FormatProject Project(params (string Path, string Text)[] files)
    {
        var directory = TestPaths.NewTempDirectory();
        foreach (var (path, text) in files.Prepend(("p.fstudio.yaml", "name: p\ndefaults: {endian: big, length: varint32, nbt: java-network}\ndependencies: [minecraft]\n")))
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        var project = FormatProject.Load(directory);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        return project;
    }

    private static List<string> Errors(GeneratedOutput output) =>
        output.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();

    /// <summary>
    /// Compiles the target's output with the stand-ins and checks the CODEC of every generated struct, enum, bit field and
    /// variant with its own tag against the simulator. Returns the failures.
    /// </summary>
    private static List<string> CheckAgainstSimulator(FormatProject project, CodegenTarget target, int iterations, params GeneratedFile[] extra)
    {
        var java = JavaToolchain.Find();
        if (java == null) Assert.Skip("No JDK: set JAVA_HOME or FORMAT_STUDIO_JDK, or put javac on the PATH");
        var jars = MinecraftStubs.NettyJars();
        if (jars == null) Assert.Skip("No netty-buffer: set FORMAT_STUDIO_NETTY to a folder with netty-buffer and netty-common, or allow the download");

        var output = CodeGeneration.Generate(project, target);
        Assert.Empty(Errors(output));
        var types = project.Types.Values
            .Where(d => d is StructDef or EnumDef or BitfieldDef || d is VariantDef { Tag: not null })
            .Select(d => d.Name)
            .Where(name => output.FileOf(name) != null)
            .Order(StringComparer.Ordinal)
            .ToList();

        var codec = new Codec(project);
        var input = new StringBuilder();
        var expected = new List<(string Type, string Hex)>();
        foreach (var type in types)
        {
            for (var i = 0; i < iterations; i++)
            {
                var value = new RandomValues(project, Simulator.FuzzSeed(1, type, i)).Generate(new NamedRef(type));
                var encoded = codec.Encode(new NamedRef(type), value);
                Assert.True(encoded.Success, $"{type}: {string.Join("; ", encoded.Diagnostics)}");
                var hex = Convert.ToHexString(encoded.Bytes);
                expected.Add((type, hex));
                input.Append($"{type} {hex}\n");
            }
        }

        var directory = TestPaths.NewTempDirectory();
        var classes = java.Compile(output, [.. MinecraftStubs.Files(), .. extra, new GeneratedFile("Harness.java", Harness(types))], directory, 21, jars);
        var answers = java.Run(string.Join(Path.PathSeparator, [classes, .. jars]), "Harness", input.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected.Count, answers.Length);
        return expected.Zip(answers)
            .Where(pair => pair.First.Hex != pair.Second.Trim())
            .Select(pair => $"{pair.First.Type}: {pair.First.Hex} became {pair.Second.Trim()}")
            .ToList();
    }

    /// <summary>Reads "type hex" lines, decodes the bytes with the type's CODEC and prints what it encodes them to.</summary>
    private static string Harness(IEnumerable<string> types)
    {
        var cases = string.Concat(types.Select(t => $"            case \"{t}\": return roundTrip(generated.test.{t}.CODEC, bytes);\n"));
        return $$"""
            import io.netty.buffer.Unpooled;
            import java.io.BufferedReader;
            import java.io.InputStreamReader;
            import java.nio.charset.StandardCharsets;
            import net.minecraft.network.RegistryFriendlyByteBuf;
            import net.minecraft.network.codec.StreamCodec;

            public final class Harness {
                public static void main(String[] args) throws Exception {
                    BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
                    String line;
                    while ((line = in.readLine()) != null) {
                        String[] parts = line.split(" ", -1);
                        try {
                            System.out.println(roundTrip(parts[0], hex(parts[1])));
                        } catch (RuntimeException e) {
                            System.out.println("! " + e);
                        }
                    }
                }

                static String roundTrip(String type, byte[] bytes) {
                    switch (type) {
            {{cases}}            default: return "! unknown type " + type;
                    }
                }

                static <T> String roundTrip(StreamCodec<RegistryFriendlyByteBuf, T> codec, byte[] bytes) {
                    RegistryFriendlyByteBuf in = new RegistryFriendlyByteBuf(Unpooled.wrappedBuffer(bytes));
                    T value = codec.decode(in);
                    if (in.isReadable()) return "! " + in.readableBytes() + " bytes left";
                    RegistryFriendlyByteBuf out = new RegistryFriendlyByteBuf(Unpooled.buffer());
                    codec.encode(out, value);
                    byte[] written = new byte[out.readableBytes()];
                    out.readBytes(written);
                    StringBuilder text = new StringBuilder();
                    for (byte b : written) text.append(String.format("%02X", b & 0xFF));
                    return text.toString();
                }

                static byte[] hex(String text) {
                    byte[] bytes = new byte[text.length() / 2];
                    for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(text.substring(2 * i, 2 * i + 2), 16);
                    return bytes;
                }
            }
            """;
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    public void JavaSampleMatchesTheSimulator(string strategy)
    {
        var failures = CheckAgainstSimulator(FormatProject.Load(TestPaths.JavaSample), Target(strategy), 10);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    private static readonly (string, string)[] Payloads =
    [
        ("types/Mode.yaml", """
            enum: Mode
            type: varint32
            values: {move: 0, raycast: 1, create: 2}
            """),
        ("types/Anchor.yaml", """
            struct: Anchor
            fields:
              - id: uuid
              - mode: Mode
            """),
        ("types/Boundary.yaml", """
            packet: Boundary
            id: mcworldgenedit:spline_manipulation_boundary
            fields:
              - mode: Mode
              - transaction: varint32
              - start: bool
              - {name: label, type: {string: utf8, max: 16}}
              - {name: targets, type: "Anchor[]", maxItems: 8}
              - {name: tag, type: nbt}
              - {name: parent, type: "Anchor?"}
              - {name: small, type: u8}
            """),
        ("types/Flagged.yaml", """
            struct: Flagged
            fields:
              - flags: u8
              - {name: extra, type: i32le, if: "flags & 1"}
              - {name: count, type: u8, computed: len(names)}
              - {name: names, type: string, length: count}
            """),
    ];

    [Fact]
    public void PayloadsAreCompositeRecords()
    {
        var output = CodeGeneration.Generate(Project(Payloads), Target());
        Assert.Empty(Errors(output));
        var boundary = output.FileOf("Boundary")!.Content;
        Assert.Contains("public record Boundary(Mode mode, int transaction, boolean start, String label, java.util.List<Anchor> targets, net.minecraft.nbt.Tag tag, Anchor parent, int small)", boundary);
        Assert.Contains("implements FormatTextPacket, CustomPacketPayload", boundary);
        Assert.Contains("""
                public static final StreamCodec<RegistryFriendlyByteBuf, Boundary> CODEC = StreamCodec.composite(
                        Mode.CODEC, Boundary::mode,
                        ByteBufCodecs.VAR_INT, Boundary::transaction,
                        ByteBufCodecs.BOOL, Boundary::start,
                        ByteBufCodecs.stringUtf8(16), Boundary::label,
                        Anchor.CODEC.apply(ByteBufCodecs.list(8)), Boundary::targets,
                        net.minecraft.network.codec.ByteBufCodecs.TAG, Boundary::tag,
                        NettyFormat.nullable(Anchor.CODEC), Boundary::parent,
                        ByteBufCodecs.BYTE.map(b -> b & 0xFF, i -> (byte) (int) i), Boundary::small,
                        Boundary::new);
            """.Replace("\r\n", "\n"), boundary);
        Assert.Contains("public static final CustomPacketPayload.Type<Boundary> TYPE = new CustomPacketPayload.Type<>(Identifier.parse(ID));", boundary);
        Assert.Contains("import net.minecraft.network.protocol.common.custom.CustomPacketPayload;", boundary);
        Assert.Contains("UUIDUtil.STREAM_CODEC, Anchor::id,", output.FileOf("Anchor")!.Content);
        Assert.Contains("CODEC = NettyFormat.codec(Flagged::read, Flagged::write);", output.FileOf("Flagged")!.Content);
        Assert.Contains("CODEC = NettyFormat.codec(Mode::read, Mode::write);", output.FileOf("Mode")!.Content);
        Assert.Contains(output.Files, f => f.Path == "formatstudio/runtime/NettyFormat.java");
        Assert.Contains(output.Files, f => f.Path == "generated/test/natives/NettyNbt.java");
    }

    [Fact]
    public void PayloadsMatchTheSimulator()
    {
        var failures = CheckAgainstSimulator(Project(Payloads), Target(), 20);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void CompositeStrategyRejectsWhatItCantCompose()
    {
        var errors = Errors(CodeGeneration.Generate(Project(Payloads), Target("composite")));
        Assert.Equal(["Flagged: can't be a StreamCodec.composite: count is computed"], errors);
    }

    [Fact]
    public void OnlyNetworkNbtHasMinecraftsTags()
    {
        var project = Project(("types/Saved.yaml", "struct: Saved\nfields:\n  - {name: data, type: nbt(java)}\n"));
        Assert.Equal(["Saved: netty reads and writes NBT as Minecraft's network NBT, nbt(java-network), not nbt(java)"],
            Errors(CodeGeneration.Generate(project, Target())));
    }

    /// <summary>A codec-only binding (Minecraft's Vec3), inside a composite and inside a manual codec.</summary>
    [Fact]
    public void CodecOnlyBindingsWorkInBothStrategies()
    {
        var project = Project(
            ("types/Vec.yaml", "struct: Vec\nfields:\n  - x: f64\n  - y: f64\n  - z: f64\n"),
            ("types/Teleport.yaml", "struct: Teleport\nfields:\n  - to: Vec\n  - yaw: f32\n"),
            ("types/Maybe.yaml", "struct: Maybe\nfields:\n  - has: bool\n  - {name: to, type: Vec, if: has}\n  - after: Vec\n"),
            ("bind/Vec.yaml", """
                bind: Vec
                codegen:
                  netty:
                    type: test.Vec3
                    codec: test.Vec3.STREAM_CODEC
                """));
        var output = CodeGeneration.Generate(project, Target());
        Assert.Empty(Errors(output));
        Assert.Null(output.FileOf("Vec"));
        Assert.Contains("test.Vec3.STREAM_CODEC, Teleport::to,", output.FileOf("Teleport")!.Content);
        Assert.Contains("NettyFormat.decode(test.Vec3.STREAM_CODEC, reader)", output.FileOf("Maybe")!.Content);

        var failures = CheckAgainstSimulator(project, Target(), 20, new GeneratedFile("test/Vec3.java", """
            package test;

            import io.netty.buffer.ByteBuf;
            import net.minecraft.network.codec.StreamCodec;

            public record Vec3(double x, double y, double z) {
                public static final StreamCodec<ByteBuf, Vec3> STREAM_CODEC = StreamCodec.of(
                    (buf, v) -> buf.writeDouble(v.x).writeDouble(v.y).writeDouble(v.z),
                    buf -> new Vec3(buf.readDouble(), buf.readDouble(), buf.readDouble()));
            }
            """));
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>The stand-ins of the classes the library's definitions are bound to, with their real wire formats.</summary>
    private static GeneratedFile[] LibraryClasses() =>
    [
        new("net/minecraft/world/phys/Vec3.java", """
            package net.minecraft.world.phys;

            import io.netty.buffer.ByteBuf;
            import net.minecraft.network.codec.StreamCodec;

            public record Vec3(double x, double y, double z) {
                public static final StreamCodec<ByteBuf, Vec3> STREAM_CODEC = StreamCodec.of(
                    (buf, v) -> buf.writeDouble(v.x).writeDouble(v.y).writeDouble(v.z),
                    buf -> new Vec3(buf.readDouble(), buf.readDouble(), buf.readDouble()));
            }
            """),
        new("net/minecraft/core/BlockPos.java", """
            package net.minecraft.core;

            import io.netty.buffer.ByteBuf;
            import net.minecraft.network.codec.StreamCodec;

            public record BlockPos(long packed) {
                public static final StreamCodec<ByteBuf, BlockPos> STREAM_CODEC = StreamCodec.of(
                    (buf, p) -> buf.writeLong(p.packed),
                    buf -> new BlockPos(buf.readLong()));
            }
            """),
        new("net/minecraft/world/level/ChunkPos.java", """
            package net.minecraft.world.level;

            import io.netty.buffer.ByteBuf;
            import net.minecraft.network.codec.StreamCodec;

            public record ChunkPos(int x, int z) {
                public static final StreamCodec<ByteBuf, ChunkPos> STREAM_CODEC = StreamCodec.of(
                    (buf, p) -> buf.writeLong((p.x & 0xFFFFFFFFL) | ((p.z & 0xFFFFFFFFL) << 32)),
                    buf -> { long l = buf.readLong(); return new ChunkPos((int) l, (int) (l >>> 32)); });
            }
            """),
    ];

    /// <summary>The library's Vec3, BlockPos, ChunkPos and Identifier use Minecraft's classes and codecs in netty.</summary>
    [Fact]
    public void TheLibrarysDefinitionsAreBoundToMinecraftsClasses()
    {
        var project = Project(("types/Look.yaml", """
            packet: Look
            id: mod:look
            fields:
              - eye: minecraft.Vec3
              - block: minecraft.BlockPos
              - chunk: minecraft.ChunkPos
              - {name: sound, type: minecraft.Identifier}
            """));
        var output = CodeGeneration.Generate(project, Target());
        Assert.Empty(Errors(output));
        var look = output.FileOf("Look")!.Content;
        Assert.Contains("net.minecraft.world.phys.Vec3 eye", look);
        Assert.Contains("net.minecraft.world.phys.Vec3.STREAM_CODEC, Look::eye,", look);
        Assert.Contains("net.minecraft.core.BlockPos.STREAM_CODEC, Look::block,", look);
        Assert.Contains("net.minecraft.world.level.ChunkPos.STREAM_CODEC, Look::chunk,", look);
        Assert.Contains("net.minecraft.resources.Identifier.STREAM_CODEC, Look::sound,", look);

        // Other generators generate the definitions themselves.
        var java = CodeGeneration.Generate(project, new CodegenTarget { Name = "j", Generator = "java-stream", Output = "g",
            Options = new JsonObject { ["package"] = "generated.test" } });
        Assert.Empty(Errors(java));
        Assert.NotNull(java.Files.FirstOrDefault(f => f.Path.EndsWith("Vec3.java", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheLibrarysDefinitionsHaveMinecraftsWireFormats()
    {
        var project = Project(("types/Look.yaml", """
            struct: Look
            fields:
              - eye: minecraft.Vec3
              - block: minecraft.BlockPos
              - chunk: minecraft.ChunkPos
              - sound: minecraft.Identifier
            """));
        var failures = CheckAgainstSimulator(project, Target(), 20, LibraryClasses());
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void TheMinecraftLibraryProvidesTheGenerator()
    {
        Assert.NotNull(Project().Generators.Find("netty"));
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), "name: p\n");
        Assert.Null(FormatProject.Load(directory).Generators.Find("netty"));
    }
}
