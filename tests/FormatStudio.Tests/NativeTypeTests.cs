using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Tests;

/// <summary>Native types: references with arguments, defaults, and a type with runtime files checked against generated code.</summary>
public class NativeTypeTests
{
    /// <summary>
    /// <c>rgb(order)</c>: three bytes shown as <c>#rrggbb</c>, stored in the given order. Generated code calls the
    /// <c>Rgb</c> class of its runtime files.
    /// </summary>
    private sealed class RgbNative : INativeType
    {
        public string Name => "rgb";

        public string Description => "A color as three bytes";

        public IReadOnlyList<NativeParameter> Parameters { get; } =
            [new("order", NativeParameterKind.Choice, "Byte order", ["rgb", "bgr"], Default: "rgb")];

        public int MinSize(NativeArguments args) => 3;

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

        public JsonNode DefaultValue(NativeArguments args) => JsonValue.Create("#000000");

        public JsonNode RandomValue(NativeArguments args, Random random, int depth) => JsonValue.Create($"#{random.Next(0x1000000):x6}");

        public NativeCodegen? Codegen(string generator) => generator switch
        {
            "csharp" or "csharp-stream" => new NativeCodegen("string",
                "global::{{namespace}}.Rgb.FromBytes({{reader}}.ReadU8(), {{reader}}.ReadU8(), {{reader}}.ReadU8(), \"{{order}}\")",
                "{{writer}}.WriteBytes(global::{{namespace}}.Rgb.ToBytes({{value}}, \"{{order}}\"))",
                [new NativeFile("Rgb.cs", """
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
                    """)]),
            "java-stream" => new NativeCodegen("String",
                "{{namespace}}.Rgb.fromBytes({{reader}}.readU8(), {{reader}}.readU8(), {{reader}}.readU8(), \"{{order}}\")",
                "{{writer}}.writeBytes({{namespace}}.Rgb.toBytes({{value}}, \"{{order}}\"))",
                [new NativeFile("Rgb.java", """
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
                    """)]),
            _ => null,
        };
    }

    private static readonly NativeTypes Natives = NativeTypes.BuiltIn.With(new RgbNative());

    private static FormatProject Project(string settings, params (string File, string Text)[] types)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        foreach (var (file, text) in types) File.WriteAllText(Path.Combine(directory, "types", file), text);
        return FormatProject.Load(directory, natives: Natives);
    }

    private static FormatProject Palette(string defaults = "") => Project($"name: p\n{defaults}sources: [types/*.yaml]\n",
        ("Palette.yaml", "struct: Palette\nfields:\n  - main: rgb\n  - reversed: rgb(bgr)\n  - colors: rgb[u8]\n  - accent: rgb?\n"));

    [Fact]
    public void ArgumentsComeFromTheReferenceTheDefaultsOrTheParameter()
    {
        var project = Palette("defaults:\n  rgb: bgr\n  nbt: java\n");
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        Assert.Equal(["bgr"], project.Bind(new NativeRef("rgb")).Args.Values);
        Assert.Equal(["rgb"], project.Bind(new NativeRef("rgb", ["rgb"])).Args.Values);
        Assert.Equal(["bgr"], project.Bind(new NativeRef("rgb", ["default"])).Args.Values);
        Assert.Equal(["java"], project.Bind(new NativeRef("nbt")).Args.Values);
        Assert.Equal(["rgb"], Palette().Bind(new NativeRef("rgb")).Args.Values);
        Assert.Contains("rgb: bgr", project.Settings.ToYaml());
    }

    [Fact]
    public void ValuesGoThroughTheNativeType()
    {
        var project = Palette();
        var codec = new Codec(project);
        var value = JsonNode.Parse("""{"main": "#102030", "reversed": "#102030", "colors": ["#ffffff"], "accent": null}""");
        var encoded = codec.Encode("Palette", value);
        Assert.Empty(encoded.Diagnostics);
        Assert.Equal("102030302010" + "01ffffff" + "00", Convert.ToHexStringLower(encoded.Bytes));
        var decoded = codec.Decode("Palette", encoded.Bytes);
        Assert.True(JsonValues.DeepEquals(value, decoded.Value));
        Assert.Contains(decoded.Root.Children, child => child.Name == "reversed" && child.Display == "#102030");

        Assert.Equal("\"#000000\"", new DefaultValues(project).Generate(new NativeRef("rgb"))?.ToJsonString());
        Assert.Contains(codec.Encode("rgb", JsonValue.Create("red")).Diagnostics, d => d.Message.Contains("Expected #rrggbb, got red"));
    }

    [Fact]
    public void InvalidReferencesAndDefaultsAreErrors()
    {
        var project = Project("name: p\ndefaults:\n  rgb: rbg\n  colour: x\nsources: [types/*.yaml]\n",
            ("A.yaml", "struct: A\nfields:\n  - a: rgb(grb)\n  - b: rgb(rgb, 1)\n  - c: hsv(1)\n  - d: {nbt: javaa}\n"));
        var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        Assert.Contains(errors, e => e.Contains("Unknown order 'grb' for rgb (rgb, bgr)"));
        Assert.Contains(errors, e => e.Contains("rgb takes 1 argument, not 2"));
        Assert.Contains(errors, e => e.Contains("Unknown native type 'hsv'"));
        Assert.Contains(errors, e => e.Contains("Unknown flavor 'javaa' for nbt"));
        Assert.Contains(errors, e => e.Contains("defaults.rgb: Unknown order 'rbg'"));
        Assert.Contains(errors, e => e.Contains("Unknown default 'colour'"));
    }

    [Fact]
    public void GeneratedCodeIncludesTheRuntimeFilesOnce()
    {
        var project = Palette();
        var output = CodeGeneration.Generate(project, new CodegenTarget { Name = "cs", Generator = "csharp", Output = "out" });
        Assert.Empty(output.Diagnostics);
        var runtime = Assert.Single(output.Files, f => f.Path == "Natives/Rgb.cs");
        Assert.StartsWith(CSharpMarker, runtime.Content);
        Assert.Contains("namespace Generated.Natives;", runtime.Content);
        Assert.Contains("\"bgr\")", output.FileOf("Palette")!.Content);

        var java = CodeGeneration.Generate(project, new CodegenTarget { Name = "java", Generator = "java-stream", Output = "out" });
        Assert.Single(java.Files, f => f.Path.EndsWith("/natives/Rgb.java", StringComparison.Ordinal));
    }

    private const string CSharpMarker = Codegen.CSharp.CSharpGenerator.FileMarker;

    [Fact]
    public void TypesWithoutCodeForAGeneratorAreReported()
    {
        var project = Project("name: p\nsources: [types/*.yaml]\n", ("A.yaml", "struct: A\nfields:\n  - c: rgb\n"));
        var natives = NativeTypes.BuiltIn.With(new NoCode());
        var copy = FormatProject.Load(project.FilePath!, natives: natives);
        var output = CodeGeneration.Generate(copy, new CodegenTarget { Name = "cs", Generator = "csharp", Output = "out" });
        Assert.Contains(output.Diagnostics, d => d.Message.Contains("The native type rgb has no code for csharp"));
    }

    private sealed class NoCode : INativeType
    {
        public string Name => "rgb";
        public string Description => "";
        public IReadOnlyList<NativeParameter> Parameters => [];
        public JsonNode? Read(INativeReader reader, NativeArguments args) => null;
        public void Write(INativeWriter writer, NativeArguments args, JsonNode? value) { }
        public JsonNode? DefaultValue(NativeArguments args) => null;
        public JsonNode? RandomValue(NativeArguments args, Random random, int depth) => null;
        public NativeCodegen? Codegen(string generator) => null;
    }

    [Theory]
    [MemberData(nameof(CodegenTests.Generators), MemberType = typeof(CodegenTests))]
    public void RuntimeFilesMatchTheSimulator(string generator)
    {
        CodegenTests.AssertMatchesSimulator(Palette(), generator, 40);
    }
}
