using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Library projects: packing, finding packages, loading with baked names and defaults, generating their code.</summary>
public class LibraryTests
{
    private static string WriteFiles(string directory, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return directory;
    }

    /// <summary>Loads a library project from its files and packs it into <paramref name="libraries"/>.</summary>
    private static string Pack(string libraries, params (string Path, string Text)[] files)
    {
        var library = FormatProject.Load(WriteFiles(TestPaths.NewTempDirectory(), files));
        Assert.False(library.Diagnostics.HasErrors, string.Join("\n", library.Diagnostics));
        return LibraryPackage.Pack(library, libraries);
    }

    private static (string Path, string Text) LibraryFile(string name, string version, string extra = "") => ($"{name}.fstudio.yaml", $"""
        name: {name}
        kind: library
        version: "{version}"
        {extra}
        """);

    /// <summary>The library geo: little endian, u8 lengths, with an enum constant in a condition and an alias.</summary>
    private static (string Path, string Text)[] Geo(string version = "1.0.0", string defaults = "endian: little\n  length: u8") =>
    [
        LibraryFile("geo", version, $"defaults:\n  {defaults}"),
        ("types/Point.yaml", """
            struct: Point
            fields:
              - x: u16
              - y: u16
              - label: string
            """),
        ("types/Shape.yaml", """
            enum: Shape
            type: u8
            values:
              circle: 1
              square: 2
            """),
        ("types/Points.yaml", """
            alias: Points
            type: Point[]
            """),
        ("types/Figure.yaml", """
            struct: Figure
            fields:
              - shape: Shape
              - name: name
                type: string
                if: shape == Shape::square
              - points: Points
            """),
    ];

    /// <summary>A big-endian project with varint lengths using geo.</summary>
    private static FormatProject Consumer(string libraries, string dependency = "- library: geo", string? extraType = null, bool placed = true)
    {
        var directory = TestPaths.NewTempDirectory();
        var files = new List<(string, string)>
        {
            ("app.fstudio.yaml", $"""
                name: app
                defaults:
                  endian: big
                libraries: [{Path.GetRelativePath(directory, libraries).Replace('\\', '/')}]
                dependencies:
                  {dependency.Replace("\n", "\n  ")}
                """),
            ("types/Placed.yaml", """
                struct: Placed
                fields:
                  - figure: geo.Figure
                  - at: Point
                  - corner: {type: Shape, as: u16}
                """),
        };
        if (!placed) files.RemoveAt(1);
        if (extraType != null) files.Add(("types/Extra.yaml", extraType));
        return FormatProject.Load(WriteFiles(directory, files.ToArray()));
    }

    private static void AssertValid(FormatProject project) =>
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));

    private const string PlacedValue = """
        {"figure": {"shape": "square", "name": "ab", "points": [{"x": 1, "y": 2, "label": "c"}]},
         "at": {"x": 258, "y": 0, "label": ""}, "corner": "circle"}
        """;

    [Fact]
    public void PackedLibrariesKeepTheirDefaults()
    {
        var libraries = TestPaths.NewTempDirectory();
        Assert.Equal(Path.Combine(libraries, "geo-1.0.0.fslib"), Pack(libraries, Geo()));
        var project = Consumer(libraries);
        AssertValid(project);
        Assert.Equal("1.0.0", project.Dependencies["geo"].Version);

        var codec = new Codec(project);
        var encoded = codec.Encode("Placed", JsonNode.Parse(PlacedValue));
        Assert.True(encoded.Success, string.Join("\n", encoded.Diagnostics));
        // Library types: little endian, u8 lengths; the project's own 'as: u16' is big endian.
        Assert.Equal("02" + "02" + "6162" + "01" + "0100" + "0200" + "01" + "63" + "0201" + "0000" + "00" + "0001", Hex.Format(encoded.Bytes));
        var decoded = codec.Decode("Placed", encoded.Bytes);
        Assert.True(JsonValues.DeepEquals(JsonNode.Parse(PlacedValue), decoded.Value), decoded.Value?.ToJsonString());
    }

    [Fact]
    public void BakedDefinitionsAreQualified()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        var project = Consumer(libraries);
        var figure = (StructDef)project.Find("geo.Figure")!;
        Assert.Equal(new NamedRef("geo.Shape"), figure.Fields[0].Type);
        Assert.Equal(new StringRef(StringEncoding.Utf8, new LengthSpec(LengthKind.Prefix, new PrimitiveRef(PrimitiveKind.U8))), figure.Fields[1].Type);
        Assert.Equal("(shape == geo.Shape::square)", figure.Fields[1].If);
        Assert.Equal(new NamedRef("geo.Points"), figure.Fields[2].Type);
        Assert.Equal(new PrimitiveRef(PrimitiveKind.U16, Endian.Little), ((StructDef)project.Find("Point")!).Fields[0].Type);
    }

    [Fact]
    public void LibrariesCanInheritDefaults()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo(defaults: "endian: little\n  length: u8\n  inherit: [endian]"));
        var project = Consumer(libraries);
        AssertValid(project);
        var encoded = new Codec(project).Encode("Point", JsonNode.Parse("""{"x": 258, "y": 1, "label": ""}"""));
        Assert.Equal("0102" + "0001" + "00", Hex.Format(encoded.Bytes));
    }

    [Theory]
    [InlineData("nbt: java", "java")]
    [InlineData("nbt: java\n  inherit: [nbt]", "bedrock-network")]
    public void NativeTypeDefaultsAreBakedUnlessInherited(string defaults, string flavor)
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, LibraryFile("tags", "1", $"defaults:\n  {defaults}"),
            ("types/Tagged.yaml", "struct: Tagged\nfields:\n  - a: nbt\n  - b: nbt(java-network)\n"));
        var directory = TestPaths.NewTempDirectory();
        WriteFiles(directory, ("app.fstudio.yaml", $"""
            name: app
            defaults:
              nbt: bedrock-network
            libraries: [{Path.GetRelativePath(directory, libraries).Replace('\\', '/')}]
            dependencies:
              - library: tags
            """));
        var project = FormatProject.Load(directory);
        AssertValid(project);
        var tagged = (StructDef)project.Find("tags.Tagged")!;
        Assert.Equal([flavor], project.Bind((NativeRef)tagged.Fields[0].Type).Args.Values);
        Assert.Equal(["java-network"], project.Bind((NativeRef)tagged.Fields[1].Type).Args.Values);
    }

    [Fact]
    public void VersionsAreChosen()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo("1.2.0"));
        Pack(libraries, Geo("1.10.0"));
        Pack(libraries, Geo("1.9.0"));
        Assert.Equal("1.10.0", Consumer(libraries).Dependencies["geo"].Version);
        Assert.Equal("1.9.0", Consumer(libraries, "- library: geo\n  version: 1.9.0").Dependencies["geo"].Version);

        var missing = Consumer(libraries, "- library: geo\n  version: 2.0.0");
        Assert.Contains(missing.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Library geo 2.0.0 not found"));
    }

    [Fact]
    public void AliasesRenameLibraries()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        var project = Consumer(libraries, "- library: geo\n  as: g", extraType: """
            struct: Extra
            fields:
              - p: g.Point
              - q: geo.Point
              - kind: Shape
              - name: label
                type: string
                if: kind == g.Shape::square || kind == Shape::square
            """);
        Assert.Same(project.Find("g.Point"), project.Find("geo.Point"));
        Assert.Same(project.Modules["geo"], project.Dependencies["g"]);
        AssertValid(project);
        var codec = new Codec(project);
        var p = """{"x": 1, "y": 2, "label": ""}""";
        Assert.Equal(13, codec.Encode("Extra", JsonNode.Parse($$"""{"p": {{p}}, "q": {{p}}, "kind": "square", "label": "a"}""")).Bytes.Length);
        Assert.Equal(11, codec.Encode("Extra", JsonNode.Parse($$"""{"p": {{p}}, "q": {{p}}, "kind": "circle"}""")).Bytes.Length);
        foreach (var generator in new[] { "csharp", "java-stream" })
            Assert.False(CodeGeneration.Generate(project, Target(generator)).Diagnostics.HasErrors);
    }

    [Fact]
    public void DependenciesOfLibrariesAreLoaded()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        Pack(libraries,
            LibraryFile("shapes", "0.1", $"dependencies:\n  - library: geo\n    as: g\ndefaults:\n  endian: big\nlibraries: [\"{libraries.Replace('\\', '/')}\"]"),
            ("types/Polygon.yaml", """
                struct: Polygon
                fields:
                  - kind: Shape
                  - corners: g.Point[u8]
                  - size: u32
                """));
        var project = Consumer(libraries, "- library: shapes", placed: false, extraType: """
            struct: Extra
            fields:
              - polygon: Polygon
              - point: geo.Point
            """);
        AssertValid(project);
        var polygon = (StructDef)project.Find("Polygon")!;
        Assert.Equal(new NamedRef("geo.Shape"), polygon.Fields[0].Type);
        Assert.Equal(new PrimitiveRef(PrimitiveKind.U32, Endian.Big), polygon.Fields[2].Type);
        Assert.Contains("geo", project.Modules.Keys);
        Assert.DoesNotContain("geo", project.Dependencies.Keys);
        Assert.Contains("Unknown type 'Point'", project.Resolve("Point").Error);
    }

    [Fact]
    public void BuiltinLibrariesComeAfterTheLibraryFolders()
    {
        var builtins = TestPaths.NewTempDirectory();
        Pack(builtins, Geo("2.0.0"));
        using var stream = File.OpenRead(Path.Combine(builtins, "geo-2.0.0.fslib"));
        var builtin = LibraryPackage.Read(stream, "built-in geo");

        var directory = WriteFiles(TestPaths.NewTempDirectory(), ("app.fstudio.yaml", "name: app\ndependencies:\n  - library: geo"));
        var libraries = Path.Combine(directory, "libraries");
        var catalog = new LibraryCatalog([libraries], FileSandbox.Unrestricted, [builtin]);
        var project = new FormatProject(ProjectSettings.Parse(File.ReadAllText(Path.Combine(directory, "app.fstudio.yaml")), "app.fstudio.yaml"), directory);
        LibraryLoader.LoadDependencies(project, catalog);
        Assert.Equal("built-in geo", project.Dependencies["geo"].Origin);

        Pack(libraries, Geo("1.0.0"));
        Assert.Equal("1.0.0", catalog.Find("geo", "1.0.0")!.Version);
        Assert.Equal("2.0.0", catalog.Find("geo", null)!.Version);
        Assert.Equal(["geo-1.0.0.fslib", "built-in geo"], catalog.Candidates("geo").Select(p => p.Origin));
    }

    [Fact]
    public void LibraryCyclesAreReported()
    {
        // Packages that can't be packed from projects (each needs the other), so they are zipped directly.
        var libraries = TestPaths.NewTempDirectory();
        foreach (var (name, other) in new[] { ("a", "b"), ("b", "a") })
        {
            using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(libraries, $"{name}-1.fslib"), System.IO.Compression.ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry($"{name}.fstudio.yaml").Open());
            writer.Write(LibraryFile(name, "1", $"dependencies:\n  - library: {other}").Text);
        }

        var project = Consumer(libraries, "- library: a");
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("a -> b -> a"));
    }

    [Fact]
    public void LibrariesCantHavePackets()
    {
        var project = FormatProject.Load(WriteFiles(TestPaths.NewTempDirectory(),
            LibraryFile("lib", "1"),
            ("Ping.yaml", "packet: Ping\nid: 1\nfields:\n  - n: u8")));
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Libraries can't have packets"));
        Assert.Throws<FormatDefinitionException>(() => LibraryPackage.Pack(project, TestPaths.NewTempDirectory()));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = ProjectSettings.Parse("""
            name: app
            kind: library
            version: "1"
            defaults:
              nbt: java
              inherit: [string, nbt, endian]
            dependencies:
              - library: geo
                version: "1.0"
                as: g
                codegen: {skip: true, namespace: Geo.Types, package: geo.types}
            libraries: [libs, ../shared]
            """, "app.fstudio.yaml");
        var again = ProjectSettings.Parse(settings.ToYaml(), "app.fstudio.yaml");
        Assert.Equal(ProjectKind.Library, again.Kind);
        Assert.Equal(["endian", "nbt", "string"], again.Defaults.Inherit.Order());
        Assert.Equal(["java"], again.Defaults.Natives["nbt"]);
        var dependency = Assert.Single(again.Dependencies);
        Assert.Equal(("geo", "1.0", "g", true, "Geo.Types", "geo.types"),
            (dependency.Library, dependency.Version, dependency.Alias, dependency.SkipCodegen, dependency.Namespace, dependency.Package));
        Assert.Equal(["libs", "../shared"], again.Libraries);
    }

    private static CodegenTarget Target(string generator) => new()
    {
        Name = generator, Generator = generator, Output = "generated",
        Options = generator.StartsWith("java", StringComparison.Ordinal)
            ? new JsonObject { ["package"] = "app.generated" }
            : new JsonObject { ["namespace"] = "App.Generated" },
    };

    [Theory]
    [InlineData("csharp")]
    [InlineData("csharp-stream")]
    public void GeneratedCSharpIncludesUsedLibraryTypes(string generator)
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        Pack(libraries, LibraryFile("unused", "1"), ("Thing.yaml", "struct: Thing\nfields:\n  - n: u8"));
        var project = Consumer(libraries, "- library: geo\n- library: unused");
        AssertValid(project);
        var output = CodeGeneration.Generate(project, Target(generator));
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        Assert.Equal(["Placed", "geo.Figure", "geo.Point", "geo.Shape"],
            output.Files.Select(f => f.TypeName).OfType<string>().Order(StringComparer.Ordinal));
        Assert.Contains("namespace App.Generated.Geo;", output.FileOf("geo.Point")!.Content);

        var runtime = typeof(FormatReader).Assembly;
        Assert.Empty(GeneratedCodeVerifier.CheckAgainstSimulator(project, Target(generator), runtime, iterations: 5));
    }

    [Fact]
    public void GeneratedJavaIncludesUsedLibraryTypes()
    {
        var java = JavaToolchain.Find();
        if (java == null) Assert.Skip("No JDK: set JAVA_HOME or FORMAT_STUDIO_JDK, or put javac on the PATH");
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        var project = Consumer(libraries);
        Assert.Empty(GeneratedCodeVerifier.CheckJavaAgainstSimulator(project, Target("java-stream"), java, iterations: 5));
    }

    [Fact]
    public void SkippedLibrariesAreReferencedInTheirNamespace()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo());
        var project = Consumer(libraries, "- library: geo\n  codegen: {skip: true, namespace: Hand.Geo, package: hand.geo}");
        AssertValid(project);

        var csharp = CodeGeneration.Generate(project, Target("csharp"));
        Assert.Equal(["Placed"], csharp.Files.Select(f => f.TypeName).OfType<string>());
        Assert.Contains("global::Hand.Geo.Point", csharp.FileOf("Placed")!.Content);

        var java = CodeGeneration.Generate(project, Target("java-stream"));
        Assert.Equal(["Placed"], java.Files.Select(f => f.TypeName).OfType<string>());
        Assert.Contains("hand.geo.Point.read(", java.FileOf("Placed")!.Content);
    }

    [Fact]
    public void CatalogListsEveryPackage()
    {
        var libraries = TestPaths.NewTempDirectory();
        Pack(libraries, Geo("1.0.0"));
        Pack(libraries, Geo("1.2.0"));
        File.WriteAllText(Path.Combine(libraries, "broken-1.0.0.fslib"), "not a zip");

        var all = new LibraryCatalog([libraries], FileSandbox.Unrestricted, builtins: []).All().Select(p => p.ToString()).ToList();
        Assert.Equal(["geo 1.0.0", "geo 1.2.0"], all);
    }
}
