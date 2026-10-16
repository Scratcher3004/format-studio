using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FormatStudio.Tests;

/// <summary>
/// Native types with type parameters (<c>xor(T, key)</c>): parsing and writing type arguments, the simulator reading
/// nested values from other bytes, generated code passing read and write functions, and libraries baking them.
/// </summary>
public class NativeTypeParameterTests
{
    /// <summary>
    /// <c>xor(T, key)</c>: a varuint32 length, then the bytes of a <c>T</c> XORed with <c>key</c>. A stand-in for
    /// compression, whose bytes would differ between the .NET and Java libraries.
    /// </summary>
    private const string PluginSource = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Nodes;
        using FormatStudio.Model;
        using FormatStudio.Natives;
        using FormatStudio.Simulation;

        namespace Masks;

        public sealed class XorType : INativeType
        {
            public string Name => "xor";
            public string Description => "";
            public IReadOnlyList<NativeParameter> Parameters => [];

            private static void Mask(byte[] bytes, NativeArguments args)
            {
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)args.Integer("key");
            }

            public JsonNode? Read(INativeReader reader, NativeArguments args)
            {
                var length = (int)reader.Wire.ReadInteger(PrimitiveKind.VarUInt32, Endian.Little);
                var bytes = reader.Wire.Take(length).ToArray();
                Mask(bytes, args);
                return reader.Decode(args.Type("T"), bytes, "content");
            }

            public void Write(INativeWriter writer, NativeArguments args, JsonNode? value)
            {
                var bytes = writer.Encode(args.Type("T"), value, "content");
                Mask(bytes, args);
                writer.Wire.WriteInteger(PrimitiveKind.VarUInt32, Endian.Little, bytes.Length);
                writer.Wire.WriteBytes(bytes);
            }

            public JsonNode? DefaultValue(NativeArguments args) => args.DefaultValue("T");

            public JsonNode? RandomValue(NativeArguments args, Random random, int depth) => args.RandomValue("T", random, depth);

            public NativeCodegen? Codegen(string generator) => null;
        }
        """;

    private const string Declaration = """
        native: xor
        plugin: ../plugins/Masks.dll
        class: Masks.XorType
        minSize: 1
        parameters:
          - name: T
            kind: type
            doc: The masked value
          - name: key
            kind: integer
            default: 90
        codegen:
          csharp:
            type: '{{T:type}}'
            read: 'global::{{namespace}}.Xor.Read({{pass-reader}}, {{key}}, {{T:read}})'
            write: 'global::{{namespace}}.Xor.Write({{pass-writer}}, {{key}}, {{value}}, {{T:write}})'
            files: csharp/Xor.cs
          csharp-stream:
            type: '{{T:type}}'
            read: 'global::{{namespace}}.Xor.Read({{pass-reader}}, {{key}}, {{T:read}})'
            write: 'global::{{namespace}}.Xor.Write({{pass-writer}}, {{key}}, {{value}}, {{T:write}})'
            files: csharp-stream/Xor.cs
          java-stream:
            type: '{{T:type}}'
            read: '{{namespace}}.Xor.read({{reader}}, {{key}}, {{T:read}})'
            write: '{{namespace}}.Xor.write({{writer}}, {{key}}, {{value}}, {{T:write}})'
            files: java/Xor.java
        """;

    private const string CSharpRuntime = """
        namespace {{namespace}};

        public delegate T XorRead<T>(ref global::{{runtime}}.FormatReader reader);
        public delegate void XorWrite<T>(ref global::{{runtime}}.FormatWriter writer, T value);

        public static class Xor
        {
            public static T Read<T>(ref global::{{runtime}}.FormatReader reader, int key, XorRead<T> read)
            {
                var bytes = reader.ReadBytes(checked((int)reader.ReadVarUInt32()));
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)key;
                var inner = new global::{{runtime}}.FormatReader(bytes);
                var value = read(ref inner);
                if (inner.Remaining > 0) throw new global::{{runtime}}.FormatDataException("Bytes left in xor");
                return value;
            }

            public static void Write<T>(ref global::{{runtime}}.FormatWriter writer, int key, T value, XorWrite<T> write)
            {
                var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
                var inner = new global::{{runtime}}.FormatWriter(buffer);
                write(ref inner, value);
                var bytes = buffer.WrittenSpan.ToArray();
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)key;
                writer.WriteVarUInt32((uint)bytes.Length);
                writer.WriteBytes(bytes);
            }
        }
        """;

    private const string CSharpStreamRuntime = """
        namespace {{namespace}};

        public static class Xor
        {
            public static T Read<T>(global::{{runtime}}.FormatStreamReader reader, int key, global::System.Func<global::{{runtime}}.FormatStreamReader, T> read)
            {
                var bytes = reader.ReadBytes(checked((int)reader.ReadVarUInt32()));
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)key;
                var inner = new global::{{runtime}}.FormatStreamReader(new global::System.IO.MemoryStream(bytes));
                var value = read(inner);
                if (!inner.AtEnd) throw new global::{{runtime}}.FormatDataException("Bytes left in xor");
                return value;
            }

            public static void Write<T>(global::{{runtime}}.FormatStreamWriter writer, int key, T value, global::System.Action<global::{{runtime}}.FormatStreamWriter, T> write)
            {
                var buffer = new global::System.IO.MemoryStream();
                write(new global::{{runtime}}.FormatStreamWriter(buffer), value);
                var bytes = buffer.ToArray();
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)key;
                writer.WriteVarUInt32((uint)bytes.Length);
                writer.WriteBytes(bytes);
            }
        }
        """;

    private const string JavaRuntime = """
        package {{namespace}};

        import formatstudio.runtime.FormatDataException;
        import formatstudio.runtime.FormatReader;
        import formatstudio.runtime.FormatWriter;
        import java.io.ByteArrayOutputStream;
        import java.io.IOException;

        public final class Xor {
            private Xor() {
            }

            public static <T> T read(FormatReader reader, int key, FormatReader.ValueReader<T> read) throws IOException {
                byte[] bytes = reader.readBytes((int) reader.readVarUInt32());
                for (int i = 0; i < bytes.length; i++) bytes[i] ^= (byte) key;
                FormatReader inner = new FormatReader(bytes);
                T value = read.read(inner);
                if (inner.remaining() > 0) throw new FormatDataException("Bytes left in xor");
                return value;
            }

            public static <T> void write(FormatWriter writer, int key, T value, FormatWriter.ValueWriter<T> write) throws IOException {
                ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                write.write(new FormatWriter(buffer), value);
                byte[] bytes = buffer.toByteArray();
                for (int i = 0; i < bytes.length; i++) bytes[i] ^= (byte) key;
                writer.writeVarUInt32(bytes.length);
                writer.writeBytes(bytes);
            }
        }
        """;

    private const string Types = """
        struct: Packet
        fields:
          - inner: xor(Inner)
          - number: xor(u16, 7)
          - names: xor(string[u8])
          - nested: xor(xor(Inner?, 1))
          - maybe: xor(Inner)?
          - shade: {native: xor, args: [{type: Shade, as: u16}]}
        ---
        struct: Inner
        fields:
          - x: i32
          - label: string
        ---
        enum: Shade
        values: {light: 0, dark: 1}
        """;

    private static readonly Lazy<byte[]> Plugin = new(() =>
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Masks", [CSharpSyntaxTree.ParseText(PluginSource)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return stream.ToArray();
    });

    /// <summary>A project with the declaration of <c>xor</c>, its runtime files, the plugin and <paramref name="types"/> in <c>types/</c>.</summary>
    private static string Folder(string settings, string types = Types)
    {
        var directory = TestPaths.NewTempDirectory();
        foreach (var folder in new[] { "types", "natives/csharp", "natives/csharp-stream", "natives/java", "plugins" })
            Directory.CreateDirectory(Path.Combine(directory, folder));
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        var index = 0;
        foreach (var type in types.ReplaceLineEndings("\n").Split("---\n"))
            File.WriteAllText(Path.Combine(directory, "types", $"t{index++}.yaml"), type);
        File.WriteAllText(Path.Combine(directory, "natives", "xor.yaml"), Declaration);
        File.WriteAllText(Path.Combine(directory, "natives", "csharp", "Xor.cs"), CSharpRuntime);
        File.WriteAllText(Path.Combine(directory, "natives", "csharp-stream", "Xor.cs"), CSharpStreamRuntime);
        File.WriteAllText(Path.Combine(directory, "natives", "java", "Xor.java"), JavaRuntime);
        File.WriteAllBytes(Path.Combine(directory, "plugins", "Masks.dll"), Plugin.Value);
        return directory;
    }

    private const string Settings = "name: p\nsources: [types/*.yaml, natives/*.yaml]\n";

    private static void AssertNoErrors(FormatProject project) =>
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));

    [Fact]
    public void TypeArgumentsAreWrittenAsTheyWereRead()
    {
        var native = Assert.IsType<NativeRef>(TypeExpressions.Parse("xor(Inner[u8], 3)"));
        Assert.Equal(["Inner[u8]", "3"], native.Args);
        Assert.Equal("xor(Inner[u8], 3)", DslWriter.FormatType(native));
        Assert.Equal(["xor(a, b)", "Map[len(x)]"], ((NativeRef)TypeExpressions.Parse("f(xor(a, b), Map[len(x)])")).Args);
        Assert.IsType<OptionalRef>(TypeExpressions.Parse("xor(Inner)?"));

        const string Mapping = "{native: xor, args: [{type: Shade, as: u16}]}";
        var mapped = Assert.IsType<NativeRef>(DslReader.ReadType(Mapping));
        Assert.Equal(["{type: Shade, as: u16}"], mapped.Args);
        Assert.Equal(Mapping, DslWriter.FormatType(mapped));
        // The parsed arguments follow the text: changing the arguments drops them.
        Assert.Empty((mapped with { Types = [new NamedRef("X")], Args = ["Y"] }).Types);
    }

    [Fact]
    public void TheSimulatorReadsTypeArgumentsFromOtherBytes()
    {
        var project = FormatProject.Load(Folder(Settings));
        AssertNoErrors(project);
        var codec = new Codec(project);
        var value = JsonNode.Parse("""
            {"inner": {"x": 1, "label": "a"}, "number": 258, "names": ["b"], "nested": {"x": 2, "label": ""}, "maybe": null, "shade": "dark"}
            """);
        var encoded = codec.Encode("Packet", value);
        Assert.Empty(encoded.Diagnostics);
        var hex = Convert.ToHexStringLower(encoded.Bytes);
        // inner: 6 bytes (i32 1, "a") XOR 0x5a; number: u16 258 XOR 7.
        Assert.StartsWith("06" + "5b5a5a5a" + "5b3b" + "02" + "0506", hex);
        var decoded = codec.Decode("Packet", encoded.Bytes);
        Assert.Empty(decoded.Diagnostics);
        Assert.True(JsonValues.DeepEquals(value, decoded.Value), decoded.Value?.ToJsonString());
        var inner = decoded.Root.Children.Single(c => c.Name == "inner");
        Assert.Equal("content", Assert.Single(inner.Children).Name);

        Assert.Equal("""{"x":0,"label":""}""", new DefaultValues(project).Generate(new NativeRef("xor", ["Inner"]))?.ToJsonString());
        Assert.IsType<JsonObject>(new RandomValues(project, 3).Generate(new NativeRef("xor", ["Inner"])));

        // Bytes left over in the masked block are an error at the field.
        var broken = codec.Decode("xor(u8)", [0x02, 0x00, 0x00]);
        Assert.Contains(broken.Diagnostics, d => d.Message.Contains("content, at offset 1 of its 2 bytes: 1 bytes left after u8"));
    }

    [Fact]
    public void TypeArgumentsAreChecked()
    {
        var project = FormatProject.Load(Folder(Settings, "struct: Bad\nfields:\n  - n: u8\n  - a: xor(Nope)\n  - b: xor(bytes[n])\n  - c: xor\n  - d: xor(u8[)\n"));
        var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        void Has(string text) => Assert.True(errors.Any(e => e.Contains(text)), $"No error with '{text}' in:\n{string.Join("\n", errors)}");
        Has("Unknown type 'Nope'");
        Has("Length expression 'n' can only be used in a struct field");
        Has("xor needs a T");
        Has("The T of xor must be a type");
    }

    public static TheoryData<string> Generators => CodegenTests.Generators;

    [Theory]
    [MemberData(nameof(Generators))]
    public void GeneratedCodePassesReadAndWriteFunctions(string generator)
    {
        var project = FormatProject.Load(Folder(Settings));
        AssertNoErrors(project);
        CodegenTests.AssertMatchesSimulator(project, generator, 40);
    }

    [Fact]
    public void LibrariesBakeTypeArguments()
    {
        var library = Folder("name: masks\nkind: library\nversion: \"1.0\"\nsources: [types/*.yaml, natives/*.yaml]\ndefaults: {length: u8, endian: big}\n",
            "struct: Wrapped\nfields:\n  - text: xor(string)\n  - inner: xor(Inner)\n---\nstruct: Inner\nfields:\n  - x: u16\n");
        var package = LibraryPackage.Pack(FormatProject.Load(library), Path.Combine(library, "out"));

        var consumer = TestPaths.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(consumer, "libraries"));
        File.Copy(package, Path.Combine(consumer, "libraries", Path.GetFileName(package)));
        File.WriteAllText(Path.Combine(consumer, "c.fstudio.yaml"), "name: c\ndependencies: [{library: masks}]\ndefaults: {length: varuint32, endian: little}\n");
        File.WriteAllText(Path.Combine(consumer, "Scene.yaml"), "struct: Scene\nfields:\n  - wrapped: Wrapped\n  - own: xor(masks.Inner)\n");
        var project = FormatProject.Load(consumer);
        AssertNoErrors(project);

        var wrapped = (StructDef)project.Find("masks.Wrapped")!;
        Assert.Equal(["{string: utf8, length: u8}"], ((NativeRef)wrapped.Fields[0].Type).Args);
        Assert.Equal(["masks.Inner"], ((NativeRef)wrapped.Fields[1].Type).Args);

        var encoded = new Codec(project).Encode("Scene", JsonNode.Parse("""{"wrapped": {"text": "a", "inner": {"x": 1}}, "own": {"x": 1}}"""));
        Assert.Empty(encoded.Diagnostics);
        // The library's u8 length and big endian inside the masked bytes.
        Assert.Equal("02" + "5b3b" + "02" + "5a5b" + "02" + "5a5b", Convert.ToHexStringLower(encoded.Bytes));
        // The library's Inner is generated because the type arguments use it.
        CodegenTests.AssertMatchesSimulator(project, "csharp", 20);
        CodegenTests.AssertMatchesSimulator(project, "java-stream", 20);
    }
}
