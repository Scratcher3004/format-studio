using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

/// <summary>
/// Definitions bound to existing types (<c>bind:</c> files): loading and checks, generated code that uses the bound
/// types instead of generating them, and bindings that come with a library.
/// </summary>
public class BindingTests
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

    private static FormatProject Project(params (string Path, string Text)[] files) =>
        FormatProject.Load(WriteFiles(TestPaths.NewTempDirectory(), [Settings, .. files]));

    private static readonly (string, string) Settings = ("p.fstudio.yaml", """
        name: bound
        defaults:
          endian: big
          length: varint32
        """);

    private static CodegenTarget Target(string generator)
    {
        var options = generator.StartsWith("java", StringComparison.Ordinal)
            ? new JsonObject { ["package"] = "generated.test" }
            : new JsonObject { ["namespace"] = "Generated.Test" };
        return new CodegenTarget { Name = generator, Generator = generator, Output = "generated", Options = options };
    }

    private static List<string> Errors(GeneratedOutput output) =>
        output.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToList();

    private static readonly (string, string) Point = ("types/Point.yaml", """
        struct: Point
        fields:
          - x: f64
          - y: f64
        """);

    private static readonly (string, string) Day = ("types/Day.yaml", """
        enum: Day
        type: varint32
        values: {monday: 0, tuesday: 1, wednesday: 2, thursday: 3, friday: 4, saturday: 5, sunday: 6}
        """);

    private static readonly (string, string) Name = ("types/Name.yaml", """
        alias: Name
        type: {string: utf8, max: 40}
        """);

    /// <summary>Point, Day and Name bound to existing types for all three generators; Point and Name share a runtime file.</summary>
    private static readonly (string, string)[] Bindings =
    [
        ("bind/Point.yaml", """
            bind: Point
            doc: A point class of the application
            codegen:
              csharp:
                type: 'global::{{namespace}}.Point'
                read: 'global::{{namespace}}.Point.Read({{pass-reader}})'
                write: 'global::{{namespace}}.Point.Write({{pass-writer}}, {{value}})'
                files: [csharp/Bound.cs]
              csharp-stream:
                type: 'global::{{namespace}}.Point'
                read: 'global::{{namespace}}.Point.Read({{pass-reader}})'
                write: 'global::{{namespace}}.Point.Write({{pass-writer}}, {{value}})'
                files: [stream/Bound.cs]
              java-stream:
                type: '{{namespace}}.Point'
                read: '{{namespace}}.Point.read({{reader}})'
                write: '{{namespace}}.Point.write({{writer}}, {{value}})'
                files: [java/Point.java]
            """),
        ("bind/Day.yaml", """
            bind: Day
            codegen:
              csharp:
                type: global::System.DayOfWeek
                read: '(global::System.DayOfWeek){{reader}}.ReadVarInt32()'
                write: '{{writer}}.WriteVarInt32((int){{value}})'
                value: '(long){{value}}'
                valueType: true
              csharp-stream:
                type: global::System.DayOfWeek
                read: '(global::System.DayOfWeek){{reader}}.ReadVarInt32()'
                write: '{{writer}}.WriteVarInt32((int){{value}})'
                value: '(long){{value}}'
                valueType: true
              java-stream:
                type: java.time.DayOfWeek
                read: 'java.time.DayOfWeek.of({{reader}}.readVarInt32() + 1)'
                write: '{{writer}}.writeVarInt32({{value}}.getValue() - 1)'
                value: '({{value}}.getValue() - 1)'
            """),
        ("bind/Name.yaml", """
            bind: Name
            codegen:
              csharp:
                type: global::System.Text.StringBuilder
                read: 'global::{{namespace}}.Names.Read({{pass-reader}})'
                write: 'global::{{namespace}}.Names.Write({{pass-writer}}, {{value}})'
                files: [csharp/Bound.cs]
              csharp-stream:
                type: global::System.Text.StringBuilder
                read: 'global::{{namespace}}.Names.Read({{pass-reader}})'
                write: 'global::{{namespace}}.Names.Write({{pass-writer}}, {{value}})'
                files: [stream/Bound.cs]
              java-stream:
                type: StringBuilder
                read: '{{namespace}}.Names.read({{reader}})'
                write: '{{namespace}}.Names.write({{writer}}, {{value}})'
                files: [java/Names.java]
            """),
        ("bind/csharp/Bound.cs", CSharpRuntime("ref global::{{runtime}}.FormatReader", "ref global::{{runtime}}.FormatWriter")),
        ("bind/stream/Bound.cs", CSharpRuntime("global::{{runtime}}.FormatStreamReader", "global::{{runtime}}.FormatStreamWriter")),
        ("bind/java/Point.java", """
            package {{namespace}};

            import java.io.IOException;
            import {{runtime}}.FormatReader;
            import {{runtime}}.FormatWriter;

            public final class Point {
                public final double x;
                public final double y;

                public Point(double x, double y) {
                    this.x = x;
                    this.y = y;
                }

                public static Point read(FormatReader reader) throws IOException {
                    return new Point(reader.readF64BE(), reader.readF64BE());
                }

                public static void write(FormatWriter writer, Point value) throws IOException {
                    writer.writeF64BE(value.x);
                    writer.writeF64BE(value.y);
                }
            }
            """),
        ("bind/java/Names.java", """
            package {{namespace}};

            import java.io.IOException;
            import java.nio.charset.StandardCharsets;
            import {{runtime}}.FormatReader;
            import {{runtime}}.FormatWriter;

            public final class Names {
                public static StringBuilder read(FormatReader reader) throws IOException {
                    return new StringBuilder(reader.readUtf8(reader.readVarInt32()));
                }

                public static void write(FormatWriter writer, StringBuilder value) throws IOException {
                    byte[] bytes = value.toString().getBytes(StandardCharsets.UTF_8);
                    writer.writeVarInt32(bytes.length);
                    writer.writeBytes(bytes);
                }
            }
            """),
    ];

    /// <summary>Point bound for Java only, with its runtime file.</summary>
    private static readonly (string, string)[] JavaPoint =
    [
        ("bind/Point.yaml", """
            bind: Point
            codegen:
              java-stream:
                type: '{{namespace}}.Point'
                read: '{{namespace}}.Point.read({{reader}})'
                write: '{{namespace}}.Point.write({{writer}}, {{value}})'
                files: [java/Point.java]
            """),
        Bindings[5],
    ];

    private static string CSharpRuntime(string reader, string writer) => """
        namespace {{namespace}};

        public sealed record Point(double X, double Y)
        {
            public static Point Read(READER reader) => new(reader.ReadF64BE(), reader.ReadF64BE());

            public static void Write(WRITER writer, Point value)
            {
                writer.WriteF64BE(value.X);
                writer.WriteF64BE(value.Y);
            }
        }

        public static class Names
        {
            public static global::System.Text.StringBuilder Read(READER reader) =>
                new(reader.ReadString(reader.ReadVarInt32(), global::{{runtime}}.FormatEncoding.Utf8));

            public static void Write(WRITER writer, global::System.Text.StringBuilder value)
            {
                var bytes = global::System.Text.Encoding.UTF8.GetBytes(value.ToString());
                writer.WriteVarInt32(bytes.Length);
                writer.WriteBytes(bytes);
            }
        }
        """.Replace("READER", reader).Replace("WRITER", writer);

    /// <summary>A struct using the bound types by value, in arrays, as optional values and in a condition.</summary>
    private static readonly (string, string) Move = ("types/Move.yaml", """
        struct: Move
        fields:
          - day: Day
          - at: Point
          - path: Point[]
          - {name: name, type: Name}
          - names: Name[u8]
          - {name: note, type: string, if: "day == Day::sunday"}
          - other: Point?
          - when: Day?
        """);

    private static FormatProject Bound() => Project([Point, Day, Name, Move, .. Bindings]);

    [Theory]
    [MemberData(nameof(CodegenTests.Generators), MemberType = typeof(CodegenTests))]
    public void BoundTypesMatchTheSimulator(string generator)
    {
        var project = Bound();
        CodegenTests.AssertMatchesSimulator(project, generator, 25);

        var output = CodeGeneration.Generate(project, Target(generator));
        Assert.Empty(Errors(output));
        Assert.Null(output.FileOf("Point"));
        Assert.Null(output.FileOf("Day"));
        Assert.NotNull(output.FileOf("Move"));
        // Point and Name share their runtime file; it is written once.
        var runtime = output.Files.Where(f => f.TypeName == null && f.Content.Contains("Runtime of the binding of")).Select(f => f.Path).ToList();
        Assert.Equal(generator == "java-stream" ? ["generated/test/natives/Names.java", "generated/test/natives/Point.java"] : ["Natives/Bound.cs"],
            runtime.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BoundTypesAreUsedInsteadOfGeneratedOnes()
    {
        var java = CodeGeneration.Generate(Bound(), Target("java-stream")).FileOf("Move")!.Content;
        Assert.Contains("public generated.test.natives.Point at;", java);
        Assert.Contains("public java.util.List<generated.test.natives.Point> path;", java);
        Assert.Contains("public StringBuilder name;", java);
        Assert.Contains("public java.time.DayOfWeek when;", java);
        Assert.Contains("result.at = generated.test.natives.Point.read(reader);", java);
        Assert.Contains("generated.test.natives.Point.write(writer, this.at);", java);
        // The condition compares the wire value of the bound enum.
        Assert.Contains("(result.day.getValue() - 1)", java);
        Assert.Contains("== 6L", java);

        var csharp = CodeGeneration.Generate(Bound(), Target("csharp")).FileOf("Move")!.Content;
        Assert.Contains("public global::System.DayOfWeek? when;", csharp);
        Assert.Contains("public global::Generated.Test.Natives.Point? other;", csharp);
    }

    [Fact]
    public void ExpressionsCantLookIntoBoundTypes()
    {
        var project = Project([Point, Day, Name, .. Bindings,
            ("types/Inside.yaml", """
                struct: Inside
                fields:
                  - at: Point
                  - {name: flag, type: u8, if: "at.x > 0"}
                """),
            ("types/Length.yaml", """
                struct: Length
                fields:
                  - {name: count, type: u8, computed: len(name)}
                  - {name: name, type: Name}
                """),
            ("types/Fixed.yaml", """
                struct: Fixed
                fields:
                  - {name: day, type: Day, const: monday}
                """),
            ("types/Tagged.yaml", """
                variant: Tagged
                tag: Day
                cases:
                  - {name: first, tag: monday, type: u8}
                """),
            ("types/Wider.yaml", """
                struct: Wider
                fields:
                  - day: {type: Day, as: u8}
                """),
            ("types/Steps.yaml", """
                struct: Steps
                fields:
                  - steps: {array: Point, until: "x == 0"}
                """),
        ]);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));

        foreach (var generator in new[] { "csharp", "java-stream" })
        {
            var errors = Errors(CodeGeneration.Generate(project, Target(generator)));
            Assert.Contains(errors, e => e.StartsWith("Inside:", StringComparison.Ordinal) && e.Contains("Point is bound to an existing type"));
            Assert.Contains(errors, e => e.StartsWith("Length:", StringComparison.Ordinal) && e.Contains("len() can't be used on Name"));
            Assert.Contains(errors, e => e.StartsWith("Fixed:", StringComparison.Ordinal) && e.Contains("a const field can't be of Day"));
            Assert.Contains(errors, e => e.StartsWith("Tagged:", StringComparison.Ordinal) && e.Contains("The tag of a variant can't be Day"));
            Assert.Contains(errors, e => e.StartsWith("Wider:", StringComparison.Ordinal) && e.Contains("can't be changed with 'as'"));
            Assert.Contains(errors, e => e.StartsWith("Steps:", StringComparison.Ordinal) && e.Contains("can't end on elements of Point"));
        }
    }

    [Fact]
    public void BoundPacketsAreLeftOutOfTheRegistry()
    {
        var project = Project([Point, .. JavaPoint,
            ("types/Hello.yaml", """
                packet: Hello
                id: 1
                fields:
                  - {name: name, type: string}
                """),
            ("bind/Hello.yaml", """
                bind: Hello
                codegen:
                  java-stream:
                    type: String
                    read: '{{reader}}.readUtf8({{reader}}.readVarInt32())'
                    write: '{{writer}}.writeUtf8({{value}})'
                """),
        ]);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var output = CodeGeneration.Generate(project, Target("java-stream"));
        Assert.Empty(Errors(output));
        Assert.Contains(output.Diagnostics, d => d.Severity == Severity.Warning && d.Message == "Hello is bound to an existing type and left out of the packet registry");
        Assert.DoesNotContain("Hello", output.Files.Single(f => f.Path.EndsWith("Packets.java", StringComparison.Ordinal)).Content);
    }

    [Fact]
    public void BindingsAreChecked()
    {
        var project = Project([Point, .. JavaPoint,
            ("bind/Unknown.yaml", """
                bind: Nowhere
                codegen:
                  java-stream: {type: String, read: a, write: b}
                """),
            ("bind/Twice.yaml", """
                bind: Point
                codegen:
                  java-stream: {type: String, read: a, write: b}
                """),
            ("bind/Half.yaml", """
                bind: Point
                codegen:
                  csharp: {type: string, read: a}
                """),
            ("bind/Odd.yaml", """
                bind: Point
                extra: 1
                codegen:
                  my-generator: {type: string, read: a, write: b, value: c}
                """),
        ]);
        var messages = project.Diagnostics.Select(d => $"{d.Severity}: {d.Message}").ToList();
        Assert.Contains("Error: Binding of Nowhere: Unknown type 'Nowhere'", messages);
        Assert.Contains("Error: Point is bound for java-stream in both bind/Point.yaml and bind/Twice.yaml", messages);
        Assert.Contains("Error: codegen.csharp of Point needs 'write'", messages);
        Assert.Contains("Warning: Unknown key 'extra' in a binding", messages);
        Assert.Contains(messages, m => m.StartsWith("Warning: Unknown generator 'my-generator' in the codegen of Point", StringComparison.Ordinal));
    }

    [Fact]
    public void CodecOnlyBindingsCantBeReadOrWritten()
    {
        var project = Project([Point, Day,
            ("types/Use.yaml", """
                struct: Use
                fields:
                  - at: Point
                """),
            ("bind/Point.yaml", """
                bind: Point
                codegen:
                  java-stream:
                    type: net.minecraft.world.phys.Vec3
                    codec: net.minecraft.world.phys.Vec3.STREAM_CODEC
                """),
        ]);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        var codegen = project.Binding(project.Find("Point")!, "java-stream")!;
        Assert.Equal("net.minecraft.world.phys.Vec3.STREAM_CODEC", codegen.Codec);
        Assert.False(codegen.CanReadWrite);
        Assert.Contains("Use: Point has only a codec, no read and write code", Errors(CodeGeneration.Generate(project, Target("java-stream"))));
    }

    [Fact]
    public void LibrariesBringTheirBindings()
    {
        var libraries = TestPaths.NewTempDirectory();
        var library = FormatProject.Load(WriteFiles(TestPaths.NewTempDirectory(), [
            ("geo.fstudio.yaml", """
                name: geo
                kind: library
                version: "1.0.0"
                defaults: {endian: big}
                """),
            Point, Day, .. JavaPoint]));
        Assert.False(library.Diagnostics.HasErrors, string.Join("\n", library.Diagnostics));
        LibraryPackage.Pack(library, libraries);

        var directory = TestPaths.NewTempDirectory();
        var project = FormatProject.Load(WriteFiles(directory,
            ("app.fstudio.yaml", $"""
                name: app
                libraries: [{Path.GetRelativePath(directory, libraries).Replace('\\', '/')}]
                dependencies: [geo]
                """),
            ("types/Trip.yaml", """
                struct: Trip
                fields:
                  - from: geo.Point
                  - day: Day
                """),
            // The project binds a definition of the library as well.
            ("bind/Day.yaml", """
                bind: geo.Day
                codegen:
                  java-stream:
                    type: java.time.DayOfWeek
                    read: 'java.time.DayOfWeek.of({{reader}}.readVarInt32() + 1)'
                    write: '{{writer}}.writeVarInt32({{value}}.getValue() - 1)'
                """)));
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        Assert.Equal("geo.Point", Assert.Single(project.Modules["geo"].Bindings).Definition);

        var output = CodeGeneration.Generate(project, Target("java-stream"));
        Assert.Empty(Errors(output));
        Assert.Null(output.FileOf("geo.Point"));
        Assert.Null(output.FileOf("geo.Day"));
        var trip = output.FileOf("Trip")!.Content;
        Assert.Contains("public generated.test.natives.Point from;", trip);
        Assert.Contains("public java.time.DayOfWeek day;", trip);
        Assert.Contains(output.Files, f => f.Path == "generated/test/natives/Point.java");
        // Other generators generate the library's definitions as usual.
        Assert.NotNull(CodeGeneration.Generate(project, Target("csharp")).FileOf("geo.Point"));
    }
}
