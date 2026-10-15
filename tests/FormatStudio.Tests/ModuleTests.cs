using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Definitions from libraries: name resolution, the codec and generated code referencing them.</summary>
public class ModuleTests
{
    private static TypeDef Parse(string yaml) => DslReader.Read(yaml, "test.yaml", []) ?? throw new InvalidOperationException(yaml);

    private static FormatModule Library(string name, params string[] definitions) => new(name, definitions.Select(Parse));

    private static readonly string[] McTypes =
    [
        """
        struct: Position
        fields:
          - x: i16
          - y: i16
        """,
        """
        enum: Color
        type: u8
        values:
          red: 1
          blue: 2
        """,
    ];

    /// <summary>A project with the library <c>mc</c> (alias <c>mc</c>) and the given definitions of its own.</summary>
    private static FormatProject Project(params string[] definitions)
    {
        var project = FormatProject.InMemory(definitions.Select(Parse), new ProjectSettings { Defaults = { Endian = Endian.Big } });
        project.AddModule(Library("mc", McTypes), "mc");
        Validation.Validator.Validate(project);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        return project;
    }

    [Fact]
    public void QualifiedNamesParse()
    {
        Assert.Equal(new NamedRef("mc.Position"), TypeExpressions.Parse("mc.Position"));
        Assert.Equal(new ArrayRef(new NamedRef("mc.Position"), LengthSpec.Default), TypeExpressions.Parse("mc.Position[]"));
        Assert.Throws<FormatException>(() => TypeExpressions.Parse("a.b.C"));
        Assert.Throws<FormatException>(() => TypeExpressions.Parse("mc."));
        Assert.Equal(new FormatStudio.Expressions.EnumConstExpr("mc.Color", "red"), FormatStudio.Expressions.Expr.Parse("mc.Color::red"));
        Assert.Equal(new FormatStudio.Expressions.MemberExpr(new FormatStudio.Expressions.NameExpr("a"), "b"), FormatStudio.Expressions.Expr.Parse("a.b"));
    }

    [Fact]
    public void NamesResolveLocallyFirstThenInDependencies()
    {
        var project = Project("""
            struct: Color
            fields:
              - value: u32
            """);
        var library = project.Dependencies["mc"];

        Assert.Same(library.Find("Position"), project.Find("Position"));
        Assert.Same(library.Find("Position"), project.Find("mc.Position"));
        Assert.Same(project.Types["Color"], project.Find("Color"));
        Assert.Same(library.Find("Color"), project.Find("mc.Color"));
        Assert.Same(library, project.Find("mc.Color")!.Owner);
        Assert.Null(project.Types["Color"].Owner);
        Assert.Equal("mc.Color", project.Find("mc.Color")!.QualifiedName);
    }

    [Fact]
    public void ResolutionErrorsNameTheProblem()
    {
        var project = Project();
        project.AddModule(Library("geo", McTypes[0]), "g");
        project.AddModule(Library("hidden", McTypes[0]));

        Assert.Contains("ambiguous: mc.Position, g.Position", project.Resolve("Position").Error);
        Assert.Same(project.Modules["geo"].Find("Position"), project.Find("g.Position"));
        Assert.Same(project.Modules["geo"].Find("Position"), project.Find("geo.Position"));
        Assert.Same(project.Modules["hidden"].Find("Position"), project.Find("hidden.Position"));
        Assert.Contains("Unknown library 'nope'", project.Resolve("nope.Position").Error);
        Assert.Contains("has no type 'Missing'", project.Resolve("mc.Missing").Error);
        Assert.Contains("Unknown type 'Missing'", project.Resolve("Missing").Error);
        Assert.Throws<FormatDefinitionException>(() => project.AddModule(Library("mc", McTypes[1])));
    }

    [Fact]
    public void ValidatorReportsUnresolvedNames()
    {
        var project = FormatProject.InMemory([Parse("""
            struct: Uses
            fields:
              - a: nope.Position
            """)]);
        Validation.Validator.Validate(project);
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Unknown library 'nope'"));
    }

    [Fact]
    public void CodecReadsAndWritesLibraryTypes()
    {
        var project = Project("""
            struct: Block
            fields:
              - at: mc.Position
              - color: Color
            """);
        var codec = new Codec(project);
        var value = JsonNode.Parse("""{"at": {"x": 1, "y": -2}, "color": "blue"}""");
        var encoded = codec.Encode("Block", value);
        Assert.True(encoded.Success, string.Join("\n", encoded.Diagnostics));
        Assert.Equal("0001fffe02", Hex.Format(encoded.Bytes));
        var decoded = codec.Decode("Block", encoded.Bytes);
        Assert.True(JsonValues.DeepEquals(value, decoded.Value), decoded.Value?.ToJsonString());
    }

    [Fact]
    public void CopiesKeepTheLibraries()
    {
        var project = Project();
        var copy = project.With(Parse("""
            struct: Draft
            fields:
              - at: Position
            """));
        Assert.Same(project.Find("mc.Position"), copy.Find("Position"));
        Assert.Same(project.Dependencies["mc"], copy.Dependencies["mc"]);
    }

    private const string Uses = """
        struct: Uses
        fields:
          - at: Position
          - color: mc.Color
          - kind: Color
          - positions: mc.Position[]
        """;

    private const string LocalColor = """
        enum: Color
        type: u8
        values:
          green: 3
        """;

    [Fact]
    public void GeneratedCSharpQualifiesLibraryTypes()
    {
        var project = Project(Uses, LocalColor);
        var target = new CodegenTarget
        {
            Name = "csharp", Generator = "csharp", Output = "generated",
            Options = new JsonObject { ["namespace"] = "Generated.Test" },
        };
        var output = CodeGeneration.Generate(project, target);
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        var code = output.FileOf("Uses")!.Content;
        Assert.Contains("global::Generated.Test.Mc.Position at", code);
        Assert.Contains("global::Generated.Test.Mc.Color color", code);
        Assert.Contains("Color kind", code);
        Assert.Equal("Mc/Types/Position.g.cs", output.FileOf("mc.Position")!.Path);
        Assert.Contains("namespace Generated.Test.Mc;", output.FileOf("mc.Position")!.Content);
        Assert.Null(output.FileOf("Position"));

        var assembly = GeneratedCodeVerifier.Compile(output, """
            public static class Harness {
              public static byte[] Run(byte[] data) {
                var reader = new global::FormatStudio.Runtime.FormatReader(data);
                var value = global::Generated.Test.Uses.Read(ref reader);
                var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
                var writer = new global::FormatStudio.Runtime.FormatWriter(buffer);
                value.Write(ref writer);
                return buffer.WrittenSpan.ToArray();
              }
            }
            """, typeof(FormatReader).Assembly);
        var bytes = Hex.Parse("0001fffe" + "02" + "03" + "01" + "00050006");
        Assert.Equal(Hex.Format(bytes), Hex.Format((byte[])assembly.GetType("Harness")!.GetMethod("Run")!.Invoke(null, [bytes])!));
    }

    [Fact]
    public void GeneratedJavaQualifiesLibraryTypes()
    {
        var project = Project(Uses, LocalColor);
        var target = new CodegenTarget
        {
            Name = "java", Generator = "java-stream", Output = "generated",
            Options = new JsonObject { ["package"] = "generated.test" },
        };
        var output = CodeGeneration.Generate(project, target);
        Assert.False(output.Diagnostics.HasErrors, string.Join("\n", output.Diagnostics));
        var code = output.FileOf("Uses")!.Content;
        Assert.Contains("generated.test.mc.Position.read(", code);
        Assert.Contains("generated.test.mc.Color.fromValue(", code);
        Assert.Contains(" Color.fromValue(", code);
        Assert.Equal("generated/test/mc/Position.java", output.FileOf("mc.Position")!.Path);
        Assert.Contains("package generated.test.mc;", output.FileOf("mc.Position")!.Content);
    }
}
