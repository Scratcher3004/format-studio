using System.IO.Compression;
using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FormatStudio.Tests;

/// <summary>
/// Native types declared in project files and implemented in .NET plugins: loading, the plugin policy, reloading,
/// libraries that ship them, and generated code checked against the plugin.
/// </summary>
public class PluginTests
{
    /// <summary>The plugin: <c>rgb(order)</c> as in <see cref="NativeTypeTests"/>, and <c>boom</c>, which fails.</summary>
    private const string PluginSource = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Nodes;
        using FormatStudio.Natives;
        using FormatStudio.Simulation;

        namespace Colors;

        public sealed class RgbType : INativeType
        {
            public string Name => "rgb";
            public string Description => "";
            public IReadOnlyList<NativeParameter> Parameters => [];

            private static bool Reversed(NativeArguments args) => args["order"] == "bgr";

            public JsonNode Read(INativeReader reader, NativeArguments args)
            {
                byte[] bytes = [reader.Wire.ReadByte(), reader.Wire.ReadByte(), reader.Wire.ReadByte()];
                if (Reversed(args)) Array.Reverse(bytes);
                var text = "#" + Convert.ToHexStringLower(bytes);
                reader.Display(text);
                return JsonValue.Create(text);
            }

            public void Write(INativeWriter writer, NativeArguments args, JsonNode? value)
            {
                if (JsonValues.String(value) is not ['#', .. var hex] || hex.Length != 6) throw writer.Error($"Expected #rrggbb, got {value}");
                var bytes = Convert.FromHexString(hex);
                if (Reversed(args)) Array.Reverse(bytes);
                writer.Wire.WriteBytes(bytes);
            }

            public JsonNode DefaultValue(NativeArguments args) => JsonValue.Create("DEFAULT");

            public JsonNode RandomValue(NativeArguments args, Random random, int depth) => JsonValue.Create($"#{random.Next(0x1000000):x6}");

            public NativeCodegen? Codegen(string generator) => null;
        }

        public sealed class BoomType : INativeType
        {
            public string Name => "boom";
            public string Description => "";
            public IReadOnlyList<NativeParameter> Parameters => [];
            public JsonNode? Read(INativeReader reader, NativeArguments args) => throw new InvalidCastException("read");
            public void Write(INativeWriter writer, NativeArguments args, JsonNode? value) => throw new InvalidCastException("write");
            public JsonNode? DefaultValue(NativeArguments args) => null;
            public JsonNode? RandomValue(NativeArguments args, Random random, int depth) => null;
            public NativeCodegen? Codegen(string generator) => null;
        }
        """;

    private const string Declaration = """
        native: rgb
        doc: A color as three bytes
        plugin: ../plugins/Colors.dll
        class: Colors.RgbType
        minSize: 3
        parameters:
          - name: order
            choices: [rgb, bgr]
            default: rgb
            doc: Byte order
        codegen:
          csharp:
            type: string
            read: 'global::{{namespace}}.Rgb.FromBytes({{reader}}.ReadU8(), {{reader}}.ReadU8(), {{reader}}.ReadU8(), "{{order}}")'
            write: '{{writer}}.WriteBytes(global::{{namespace}}.Rgb.ToBytes({{value}}, "{{order}}"))'
            files: csharp/*.cs
          csharp-stream:
            type: string
            read: 'global::{{namespace}}.Rgb.FromBytes({{reader}}.ReadU8(), {{reader}}.ReadU8(), {{reader}}.ReadU8(), "{{order}}")'
            write: '{{writer}}.WriteBytes(global::{{namespace}}.Rgb.ToBytes({{value}}, "{{order}}"))'
            files: [csharp/*.cs]
          java-stream:
            type: String
            read: '{{namespace}}.Rgb.fromBytes({{reader}}.readU8(), {{reader}}.readU8(), {{reader}}.readU8(), "{{order}}")'
            write: '{{writer}}.writeBytes({{namespace}}.Rgb.toBytes({{value}}, "{{order}}"))'
            files: [java/Rgb.java]
        """;

    private const string CSharpRuntime = """
        namespace {{namespace}};

        public static class Rgb
        {
            public static string FromBytes(byte a, byte b, byte c, string order) =>
                order == "bgr" ? $"#{c:x2}{b:x2}{a:x2}" : $"#{a:x2}{b:x2}{c:x2}";

            public static byte[] ToBytes(string value, string order)
            {
                var bytes = global::System.Convert.FromHexString(value.Substring(1));
                if (order == "bgr") global::System.Array.Reverse(bytes);
                return bytes;
            }
        }
        """;

    private const string JavaRuntime = """
        package {{namespace}};

        public final class Rgb {
            private Rgb() {
            }

            public static String fromBytes(int a, int b, int c, String order) {
                return order.equals("bgr") ? String.format("#%02x%02x%02x", c, b, a) : String.format("#%02x%02x%02x", a, b, c);
            }

            public static byte[] toBytes(String value, String order) {
                byte[] bytes = new byte[3];
                for (int i = 0; i < 3; i++) bytes[order.equals("bgr") ? 2 - i : i] = (byte) Integer.parseInt(value.substring(1 + 2 * i, 3 + 2 * i), 16);
                return bytes;
            }
        }
        """;

    private const string Palette = "struct: Palette\nfields:\n  - main: rgb\n  - reversed: rgb(bgr)\n  - colors: rgb[u8]\n  - accent: rgb?\n";

    private static readonly Lazy<byte[]> Plugin = new(() => Compile(PluginSource));

    /// <summary>Compiles a plugin against the running Format Studio.</summary>
    private static byte[] Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Colors", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return stream.ToArray();
    }

    /// <summary>A folder with the declaration of <c>rgb</c>, its runtime files and the plugin.</summary>
    private static string Folder(string settings, string types = Palette, string declaration = Declaration, byte[]? plugin = null)
    {
        var directory = TestPaths.NewTempDirectory();
        foreach (var folder in new[] { "types", "natives/csharp", "natives/java", "plugins" }) Directory.CreateDirectory(Path.Combine(directory, folder));
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        File.WriteAllText(Path.Combine(directory, "types", "Palette.yaml"), types);
        File.WriteAllText(Path.Combine(directory, "natives", "rgb.yaml"), declaration);
        File.WriteAllText(Path.Combine(directory, "natives", "csharp", "Rgb.cs"), CSharpRuntime);
        File.WriteAllText(Path.Combine(directory, "natives", "java", "Rgb.java"), JavaRuntime);
        File.WriteAllBytes(Path.Combine(directory, "plugins", "Colors.dll"), plugin ?? Plugin.Value);
        return directory;
    }

    private const string Settings = "name: p\nsources: [types/*.yaml, natives/*.yaml]\n";

    private static void AssertNoErrors(FormatProject project) =>
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));

    [Fact]
    public void DeclaredTypesRunThePlugin()
    {
        var project = FormatProject.Load(Folder(Settings));
        AssertNoErrors(project);
        var rgb = Assert.Single(project.DeclaredNatives);
        Assert.NotNull(rgb.Implementation);
        Assert.Equal("A color as three bytes", rgb.Description);
        Assert.Equal(["natives/csharp/Rgb.cs", "natives/java/Rgb.java", "plugins/Colors.dll"], rgb.Files.Order(StringComparer.Ordinal));

        var codec = new Codec(project);
        var value = JsonNode.Parse("""{"main": "#102030", "reversed": "#102030", "colors": ["#ffffff"], "accent": null}""");
        var encoded = codec.Encode("Palette", value);
        Assert.Empty(encoded.Diagnostics);
        Assert.Equal("102030302010" + "01ffffff" + "00", Convert.ToHexStringLower(encoded.Bytes));
        Assert.True(JsonValues.DeepEquals(value, codec.Decode("Palette", encoded.Bytes).Value));
        Assert.Equal("\"DEFAULT\"", new DefaultValues(project).Generate(new NativeRef("rgb"))?.ToJsonString());
        Assert.Equal(["bgr"], project.Bind(new NativeRef("rgb", ["bgr"])).Args.Values);
    }

    [Theory]
    [MemberData(nameof(CodegenTests.Generators), MemberType = typeof(CodegenTests))]
    public void GeneratedCodeMatchesThePlugin(string generator)
    {
        CodegenTests.AssertMatchesSimulator(FormatProject.Load(Folder(Settings)), generator, 40);
    }

    [Fact]
    public void WithoutThePluginCodeIsStillGenerated()
    {
        var project = FormatProject.Load(Folder(Settings), plugins: PluginPolicy.None);
        AssertNoErrors(project);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("rgb can't decode or encode") && d.Message.Contains("plugins are disabled"));
        Assert.Null(project.DeclaredNatives[0].Implementation);

        var encoded = new Codec(project).Encode("Palette", JsonNode.Parse("""{"main": "#102030", "reversed": "#102030", "colors": [], "accent": null}"""));
        Assert.Contains(encoded.Diagnostics, d => d.Message.Contains("rgb can't decode or encode"));

        var output = CodeGeneration.Generate(project, new CodegenTarget { Name = "cs", Generator = "csharp", Output = "out" });
        Assert.Empty(output.Diagnostics);
        Assert.Contains("public static class Rgb", Assert.Single(output.Files, f => f.Path == "Natives/Rgb.cs").Content);
    }

    [Fact]
    public void AnAllowListAdmitsPluginsByHash()
    {
        var folder = Folder(Settings);
        var allowed = FormatProject.Load(folder, plugins: PluginPolicy.Parse(PluginPolicy.Hash(Plugin.Value)));
        Assert.NotNull(allowed.DeclaredNatives[0].Implementation);

        var other = FormatProject.Load(folder, plugins: PluginPolicy.Parse(new string('0', 64)));
        Assert.Null(other.DeclaredNatives[0].Implementation);
        Assert.Contains("isn't allowed (SHA-256 " + PluginPolicy.Hash(Plugin.Value), other.DeclaredNatives[0].Unavailable);
        Assert.Throws<FormatDefinitionException>(() => PluginPolicy.Parse("nope"));
    }

    [Fact]
    public void ARebuiltPluginIsLoadedOnReload()
    {
        var folder = Folder(Settings);
        var before = FormatProject.Load(folder);
        Assert.Same(before.DeclaredNatives[0].Implementation!.GetType(), FormatProject.Load(folder).DeclaredNatives[0].Implementation!.GetType());

        File.WriteAllBytes(Path.Combine(folder, "plugins", "Colors.dll"), Compile(PluginSource.Replace("\"DEFAULT\"", "\"REBUILT\"")));
        var after = FormatProject.Load(folder);
        Assert.Equal("\"REBUILT\"", new DefaultValues(after).Generate(new NativeRef("rgb"))?.ToJsonString());
        // Projects loaded before keep working with the old plugin.
        Assert.Equal("\"DEFAULT\"", new DefaultValues(before).Generate(new NativeRef("rgb"))?.ToJsonString());
    }

    [Fact]
    public void ExceptionsOfPluginsBecomeDiagnostics()
    {
        var folder = Folder(Settings, "struct: A\nfields:\n  - a: boom\n");
        File.WriteAllText(Path.Combine(folder, "natives", "boom.yaml"), "native: boom\nplugin: ../plugins/Colors.dll\nclass: Colors.BoomType\n");
        var project = FormatProject.Load(folder);
        AssertNoErrors(project);
        var codec = new Codec(project);
        Assert.Contains(codec.Encode("A", JsonNode.Parse("""{"a": 1}""")).Diagnostics, d => d.Message.Contains("boom failed: InvalidCastException: write"));
        Assert.Contains(codec.Decode("A", [1]).Diagnostics, d => d.Message.Contains("boom failed: InvalidCastException: read"));
    }

    [Fact]
    public void BrokenDeclarationsAreErrors()
    {
        var folder = Folder(Settings);
        File.WriteAllText(Path.Combine(folder, "natives", "a.yaml"), "native: missing\nplugin: ../plugins/Missing.dll\nclass: X\n");
        File.WriteAllText(Path.Combine(folder, "natives", "b.yaml"), "native: noclass\nplugin: ../plugins/Colors.dll\nclass: Colors.Nope\n");
        File.WriteAllText(Path.Combine(folder, "natives", "c.yaml"), "native: nbt\n");
        File.WriteAllText(Path.Combine(folder, "natives", "d.yaml"), "native: rgb\n");
        File.WriteAllText(Path.Combine(folder, "natives", "e.yaml"), "native: odd\nparameters: [{name: x, kind: choice}]\n");
        File.WriteAllText(Path.Combine(folder, "natives", "f.yaml"), "native: outside\nplugin: ../../x.dll\nclass: X\n");
        var errors = FormatProject.Load(folder).Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        Assert.Contains(errors, e => e.Contains("Missing.dll doesn't exist"));
        Assert.Contains(errors, e => e.Contains("noclass: plugins/Colors.dll has no class Colors.Nope"));
        Assert.Contains(errors, e => e.Contains("Two native types are named 'nbt': built-in and natives/c.yaml"));
        Assert.Contains(errors, e => e.Contains("Native type 'rgb' is also declared in natives/d.yaml") || e.Contains("Native type 'rgb' is also declared in natives/rgb.yaml"));
        Assert.Contains(errors, e => e.Contains("Parameter x needs its 'choices'"));
        Assert.Contains(errors, e => e.Contains("../../x.dll is outside the project"));
    }

    [Fact]
    public void LibrariesShipTheirNativeTypes()
    {
        var library = Folder("name: colors\nkind: library\nversion: \"1.0\"\nsources: [types/*.yaml, natives/*.yaml]\n");
        var package = LibraryPackage.Pack(FormatProject.Load(library), Path.Combine(library, "out"));
        using (var zip = ZipFile.OpenRead(package))
        {
            Assert.Equal(["natives/csharp/Rgb.cs", "natives/java/Rgb.java", "natives/rgb.yaml", "p.fstudio.yaml", "plugins/Colors.dll", "types/Palette.yaml"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        var consumer = TestPaths.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(consumer, "libraries"));
        File.Copy(package, Path.Combine(consumer, "libraries", Path.GetFileName(package)));
        File.WriteAllText(Path.Combine(consumer, "c.fstudio.yaml"), "name: c\ndependencies: [{library: colors}]\n");
        File.WriteAllText(Path.Combine(consumer, "Scene.yaml"), "struct: Scene\nfields:\n  - sky: rgb(bgr)\n  - palette: colors.Palette\n");
        var project = FormatProject.Load(consumer);
        AssertNoErrors(project);
        Assert.Empty(project.DeclaredNatives);
        Assert.IsType<DeclaredNative>(project.Natives.Find("rgb"));

        var codec = new Codec(project);
        var value = JsonNode.Parse("""{"sky": "#010203", "palette": {"main": "#102030", "reversed": "#102030", "colors": [], "accent": "#aabbcc"}}""");
        var encoded = codec.Encode("Scene", value);
        Assert.Empty(encoded.Diagnostics);
        Assert.Equal("030201" + "102030302010" + "00" + "01aabbcc", Convert.ToHexStringLower(encoded.Bytes));
        CodegenTests.AssertMatchesSimulator(project, "csharp", 20);

        var disabled = FormatProject.Load(consumer, plugins: PluginPolicy.None);
        AssertNoErrors(disabled);
        Assert.Contains(disabled.Diagnostics, d => d.Message.StartsWith("Library colors 1.0: rgb can't decode or encode", StringComparison.Ordinal));
    }
}
