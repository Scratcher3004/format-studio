using System.IO.Compression;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

/// <summary>
/// Code generators declared in project files and implemented in .NET plugins: the per-project registry, the plugin
/// policy, broken plugins and libraries that ship a generator.
/// </summary>
public class GeneratorPluginTests
{
    private const string Declaration = "generator: sketch\ndoc: Pseudo-code for reviews\nplugin: ../plugins/Sketch.dll\nclass: Sketch.SketchGenerator\n";

    private const string BrokenSource = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Nodes;
        using FormatStudio.Codegen;
        using FormatStudio.Projects;

        namespace Broken;

        public class BoomGenerator : ICodeGenerator
        {
            public string Name => "boom";
            public string Title => "Boom";
            public string Language => "text";
            public string Description => "";
            public string DefaultOutput => "generated/boom";
            public virtual string Marker => "# boom";
            public IReadOnlyList<CodegenOption> Options => [];
            public GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped) => throw new InvalidOperationException("kaputt");
        }

        public sealed class UnmarkedGenerator : BoomGenerator
        {
            public override string Marker => "";
        }

        public sealed class NotAGenerator
        {
        }
        """;

    private static readonly Lazy<byte[]> Broken = new(() => GeneratorSdkTests.Compile("Broken", BrokenSource));

    private const string Settings = "name: p\nsources: [types/*.yaml, generators/*.yaml]\ncodegen:\n  - name: review\n    generator: sketch\n    output: generated/review\n";

    private const string Header = "struct: Header\nskipCodegen: [csharp]\nfields:\n  - id: u16\n  - label: string\n";

    /// <summary>A folder with a definition, the declaration of <c>sketch</c> and the plugin.</summary>
    private static string Folder(string settings = Settings)
    {
        var directory = TestPaths.NewTempDirectory();
        foreach (var folder in new[] { "types", "generators", "plugins" }) Directory.CreateDirectory(Path.Combine(directory, folder));
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings);
        File.WriteAllText(Path.Combine(directory, "types", "Header.yaml"), Header);
        File.WriteAllText(Path.Combine(directory, "generators", "sketch.yaml"), Declaration);
        File.WriteAllBytes(Path.Combine(directory, "plugins", "Sketch.dll"), GeneratorSdkTests.SketchAssembly.Value);
        File.WriteAllBytes(Path.Combine(directory, "plugins", "Broken.dll"), Broken.Value);
        return directory;
    }

    private static void AssertNoErrors(FormatProject project) =>
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));

    [Fact]
    public void DeclaredGeneratorsGenerateAndWrite()
    {
        var folder = Folder();
        var project = FormatProject.Load(folder);
        AssertNoErrors(project);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Warning);
        var sketch = Assert.IsType<DeclaredGenerator>(project.Generators.Find("sketch"));
        Assert.Same(sketch, Assert.Single(project.DeclaredGenerators));
        Assert.NotNull(sketch.Implementation);
        Assert.Equal(("Sketch", "Pseudo-code for reviews", "generated/sketch", "# sketch"), (sketch.Title, sketch.Description, sketch.DefaultOutput, sketch.Marker));
        Assert.Equal(["ranges"], sketch.Options.Select(o => o.Key));
        Assert.Equal(["plugins/Sketch.dll"], sketch.Files);
        // The built-in generators come first, and other projects don't see the plugin's.
        Assert.Equal(["csharp", "csharp-stream", "java-stream", "sketch"], project.Generators.All.Select(g => g.Name));
        Assert.Null(CodeGenerators.BuiltIn.Find("sketch"));

        var target = project.Settings.Codegen.Single();
        CodeGeneration.Check(project, target);
        var output = CodeGeneration.Generate(project, target);
        Assert.Empty(output.Diagnostics);
        Assert.StartsWith("# sketch\nstruct Header:", output.FileOf("Header")!.Content.Replace("\r", ""));
        var summary = CodeGeneration.Write(project, target, output);
        Assert.Single(summary.Written);
        Assert.True(File.Exists(Path.Combine(folder, "generated", "review", "Header.sketch")));
        // Definitions can skip the plugin's generator by name like any other.
        Assert.Null(CodeGeneration.Generate(project.With(new StructDef { Name = "Hidden", SkipCodegen = new CodegenSkip(false, ["sketch"]) }), target).FileOf("Hidden"));
    }

    [Fact]
    public void WithoutThePluginTargetsStayValidButDontGenerate()
    {
        var folder = Folder(Settings.Replace("output: generated/review", "output: generated/review\n    options: {ranges: false}"));
        var project = FormatProject.Load(folder, plugins: PluginPolicy.None);
        AssertNoErrors(project);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("sketch can't generate code") && d.Message.Contains("plugins are disabled"));
        var sketch = Assert.IsType<DeclaredGenerator>(project.Generators.Find("sketch"));
        Assert.Null(sketch.Implementation);
        Assert.Equal(("sketch", "generated/sketch"), (sketch.Title, sketch.DefaultOutput));

        var target = project.Settings.Codegen.Single();
        CodeGeneration.Check(project, target);
        var output = CodeGeneration.Generate(project, target);
        // Only the reason, not the options it can't know.
        Assert.Equal("sketch can't generate code", Assert.Single(output.Diagnostics).Message.Split(':')[0]);
        Assert.Throws<FormatDefinitionException>(() => CodeGeneration.Write(project, target, output));
    }

    [Fact]
    public void AnAllowListAdmitsGeneratorsByHash()
    {
        var folder = Folder();
        var allowed = FormatProject.Load(folder, plugins: PluginPolicy.Parse(PluginPolicy.Hash(GeneratorSdkTests.SketchAssembly.Value)));
        Assert.NotNull(allowed.DeclaredGenerators[0].Implementation);
        var other = FormatProject.Load(folder, plugins: PluginPolicy.Parse(new string('0', 64)));
        Assert.Contains("isn't allowed (SHA-256 " + PluginPolicy.Hash(GeneratorSdkTests.SketchAssembly.Value), other.DeclaredGenerators[0].Unavailable);
    }

    [Fact]
    public void NativeTypesAndSkipCodegenMayNameDeclaredGenerators()
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "generators", "rgb.yaml"),
            "native: rgb\nminSize: 3\ncodegen:\n  sketch: {type: color, read: '{{reader}}.rgb()', write: '{{writer}}.rgb({{value}})'}\n  rust: {type: Rgb, read: r, write: w}\n");
        File.WriteAllText(Path.Combine(folder, "types", "Sky.yaml"), "struct: Sky\nskipCodegen: [sketch, nowhere]\nfields:\n  - color: rgb\n");
        var project = FormatProject.Load(folder);
        AssertNoErrors(project);
        var warnings = project.Diagnostics.Where(d => d.Severity == Severity.Warning).Select(d => d.Message).ToList();
        Assert.Contains(warnings, w => w.StartsWith("Unknown generator 'rust' in the codegen of rgb (csharp, csharp-stream, java-stream, sketch)", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("skipCodegen names 'nowhere'"));
        Assert.DoesNotContain(warnings, w => w.Contains("'sketch'"));
    }

    [Fact]
    public void BrokenGeneratorsAreReported()
    {
        var folder = Folder();
        void Declare(string file, string text) => File.WriteAllText(Path.Combine(folder, "generators", file), text);
        Declare("a.yaml", "generator: csharp\nplugin: ../plugins/Sketch.dll\nclass: Sketch.SketchGenerator\n");
        Declare("b.yaml", "generator: nope\nplugin: ../plugins/Sketch.dll\nclass: Sketch.Nope\n");
        Declare("c.yaml", "generator: plain\nplugin: ../plugins/Broken.dll\nclass: Broken.NotAGenerator\n");
        Declare("d.yaml", "generator: unmarked\nplugin: ../plugins/Broken.dll\nclass: Broken.UnmarkedGenerator\n");
        Declare("e.yaml", "generator: lonely\n");
        Declare("f.yaml", "generator: sketch\nplugin: ../plugins/Sketch.dll\nclass: Sketch.SketchGenerator\n");
        Declare("g.yaml", "generator: 'bad name'\nplugin: x.dll\nclass: X\n");
        Declare("h.yaml", "generator: boom\nplugin: ../plugins/Broken.dll\nclass: Broken.BoomGenerator\n");
        var project = FormatProject.Load(folder);
        var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();
        void Has(string text) => Assert.True(errors.Any(e => e.Contains(text)), $"No error with '{text}' in:\n{string.Join("\n", errors)}");
        Has("Two generators are named 'csharp': built-in and generators/a.yaml");
        Has("nope: plugins/Sketch.dll has no class Sketch.Nope");
        Has("plain: Broken.NotAGenerator doesn't implement FormatStudio.Codegen.ICodeGenerator");
        Has("unmarked: Broken.UnmarkedGenerator has no Marker");
        Has("Generator lonely needs the 'plugin'");
        Has("Generator 'sketch' is also declared in generators/");
        Has("'bad name' is not a valid generator name");
        // The built-in generator stays.
        Assert.IsNotType<DeclaredGenerator>(project.Generators.Find("csharp"));

        var boom = CodeGeneration.Generate(project, new CodegenTarget { Name = "b", Generator = "boom", Output = "out" });
        Assert.Contains(boom.Diagnostics, d => d.Message == "boom failed: InvalidOperationException: kaputt");
    }

    [Fact]
    public void LibrariesShipTheirGenerators()
    {
        var library = Folder("name: sketches\nkind: library\nversion: \"1.0\"\nsources: [types/*.yaml, generators/*.yaml]\n");
        var package = LibraryPackage.Pack(FormatProject.Load(library), Path.Combine(library, "out"));
        using (var zip = ZipFile.OpenRead(package))
        {
            Assert.Equal(["generators/sketch.yaml", "p.fstudio.yaml", "plugins/Sketch.dll", "types/Header.yaml"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        var consumer = TestPaths.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(consumer, "libraries"));
        File.Copy(package, Path.Combine(consumer, "libraries", Path.GetFileName(package)));
        File.WriteAllText(Path.Combine(consumer, "c.fstudio.yaml"),
            "name: c\ndependencies: [{library: sketches}]\ncodegen:\n  - name: review\n    generator: sketch\n    output: generated/review\n");
        File.WriteAllText(Path.Combine(consumer, "Scene.yaml"), "struct: Scene\nfields:\n  - header: Header\n");
        var project = FormatProject.Load(consumer);
        AssertNoErrors(project);
        Assert.Empty(project.DeclaredGenerators);
        Assert.IsType<DeclaredGenerator>(project.Generators.Find("sketch"));
        var output = CodeGeneration.Generate(project, project.Settings.Codegen.Single());
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        Assert.NotNull(output.FileOf("Scene"));

        var disabled = FormatProject.Load(consumer, plugins: PluginPolicy.None);
        AssertNoErrors(disabled);
        Assert.Contains(disabled.Diagnostics, d => d.Message.StartsWith("Library sketches 1.0: sketch can't generate code", StringComparison.Ordinal));
    }
}
