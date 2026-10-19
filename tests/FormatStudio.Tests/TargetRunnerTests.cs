using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>
/// Test cases against generated C# and Java (<c>test --target</c>, <see cref="ProjectSuite"/>). The Java runs are skipped
/// without a JDK.
/// </summary>
public class TargetRunnerTests
{
    public static TheoryData<string> Generators => ["csharp", "csharp-stream", "java-stream"];

    /// <summary>
    /// <c>Point {x: u8, y: u8}</c>, <c>Var {v: varint32}</c>, packets <c>A {a: u8}</c> (id 1) and <c>B {b: u8}</c> (id 2) in
    /// protocol X and the given test file; <paramref name="protocols"/> false leaves out the protocol and adds
    /// <c>C {c: u8}</c>, also with id 1.
    /// </summary>
    private static FormatProject Project(string tests, bool protocols = true)
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        if (protocols) File.AppendAllText(Path.Combine(directory, "test" + ProjectSettings.Extension), "protocols:\n  - name: X\n    groups: [x]\n");
        else File.WriteAllText(Path.Combine(directory, "packets", "C.yaml"), "packet: C\nid: 1\nfields:\n  - c: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), "struct: Point\nfields:\n  - x: u8\n  - y: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Var.yaml"), "struct: Var\nfields:\n  - v: varint32\n");
        File.WriteAllText(Path.Combine(directory, "packets", "A.yaml"), $"packet: A\nid: 1\n{(protocols ? "groups: [x]\n" : "")}fields:\n  - a: u8\n");
        File.WriteAllText(Path.Combine(directory, "packets", "B.yaml"), $"packet: B\nid: 2\n{(protocols ? "groups: [x]\n" : "")}fields:\n  - b: u8\n");
        File.WriteAllText(Path.Combine(directory, "tests", "tests.yaml"), tests);
        return FormatProject.Load(directory);
    }

    private static TargetTools Tools(string generator)
    {
        var tools = TargetTools.Find();
        if (generator.StartsWith("java", StringComparison.Ordinal) && tools.Java == null) Assert.Skip(JavaToolchain.Missing);
        return tools;
    }

    private static Dictionary<string, CaseResult> Run(FormatProject project, string generator) =>
        TargetRunner.Run(project, CodeGeneration.FindTarget(project, generator)!, Tools(generator)).ToDictionary(r => r.Case.Name);

    [Theory]
    [MemberData(nameof(Generators))]
    public void CasesRunAgainstTheGeneratedCode(string generator)
    {
        var project = Project("""
            - {name: bytes, type: Point, hex: 01 02}
            - {name: value, type: Point, value: {x: 3, y: 4}}
            - {name: rest, type: Point, hex: 01 02 03, expect: {consumed: 2}}
            - {name: rejected, type: Point, hex: "01", expect: {error: remaining}}
            - {name: packet, packet: {header: u8, protocol: X}, hex: 02 07}
            - {name: unknown-id, packet: {header: u8, protocol: X}, hex: 09 00, expect: {error: no packet}}
            - {name: wrong-packet, type: B, packet: {header: u8, protocol: X}, hex: 01 07, expect: {error: not B}}
            - {name: fuzz, packet: {header: u8, protocol: X}, fuzz: {iterations: 5}}
            - {name: fuzz-point, type: Point, fuzz: true}
            - {name: long-varint, type: Var, hex: 8000}
            - {name: array, type: "Point[]", hex: 01 0102}
            - {name: encode, type: Point, value: {x: 300, y: 1}, expect: {fails: encode}}
            - {name: not-here, type: Point, hex: 01 02, skip-targets: {csharp: no reason, csharp-stream: false, java-stream: Java says no}}
            """);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Error);
        var results = Run(project, generator);

        foreach (var name in new[] { "bytes", "value", "rest", "rejected", "packet", "unknown-id", "wrong-packet", "fuzz", "fuzz-point" })
            Assert.True(results[name].Passed, $"{name}: {results[name].Summary}");
        Assert.All(results.Values, r => Assert.Equal(generator, r.Target));
        Assert.Equal($"tests/bytes [{generator}]", results["bytes"].Id);

        // The simulator re-encodes the varint the same way, and so must the generated code.
        var varint = Assert.Single(results["long-varint"].Failures);
        Assert.Equal(CaseFailureKind.RoundTrip, varint.Kind);
        Assert.Equal("the generated code writes 8000 back as 00", varint.Message);
        Assert.Equal(0, varint.Offset);

        Assert.Equal("skipped: Point[] has no generated Read of its own (structs, bitfields and variants with a tag have)", results["array"].Summary);
        Assert.True(results["encode"].Skipped);
        Assert.Equal(generator switch
        {
            "csharp" => "skipped: no reason",
            "java-stream" => "skipped: Java says no",
            _ => "ok",
        }, results["not-here"].Summary);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void PacketIdsGoThroughTheGeneratedRegistry(string generator)
    {
        // Without protocols, A and C share id 1 and the registry has only A.
        var project = Project("""
            - {name: a, type: A, packet: u8, value: {a: 7}}
            - {name: c, type: C, packet: u8, value: {c: 7}}
            """, protocols: false);
        var results = Run(project, generator);

        Assert.True(results["a"].Passed, results["a"].Summary);
        Assert.Equal("the generated registry reads id 1 as A, not C", Assert.Single(results["c"].Failures).Message);
    }

    [Fact]
    public void ReportsNameTheTarget()
    {
        var project = Project("- {name: bytes, type: Point, hex: 01 02}\n");
        var results = TestRunner.Run(project).Concat(Run(project, "csharp").Values).ToList();

        var json = JsonNode.Parse(TestReports.Json("test", results))!["cases"]!.AsArray();
        Assert.Equal([null, "csharp"], json.Select(c => c!["target"]?.GetValue<string>()));
        Assert.Contains("name=\"bytes [csharp]\"", TestReports.JUnit("test", results));
    }

    [Fact]
    public void SkipTargetsComeFromTheSuiteAndTheCase()
    {
        var project = Project("""
            suite: s
            skip-targets: [java-stream, csharp]
            cases:
              - {name: a, type: Point, hex: 01 02, skip-targets: {csharp: false, nonsense: why}}
            """);
        var test = Assert.Single(project.Tests);
        Assert.Equal(new Dictionary<string, string> { ["java-stream"] = "", ["nonsense"] = "why" }, test.SkipTargets);
        Assert.Contains(project.Diagnostics, d => d.Message == "Test a: 'skip-targets' names 'nonsense', which is neither a codegen target nor a generator");
        Assert.Equal("skipped: 'skip-targets' names the target", Run(project, "java-stream")["a"].Summary);
    }
}

/// <summary>The sample project's suite as unit tests, against the simulator and the generated C# and Java.</summary>
public class SampleSuiteTests
{
    private static readonly ProjectSuite Suite = ProjectSuite.Get(TestPaths.JavaSample, "csharp", "csharp-stream", "java-stream");

    public static IEnumerable<object[]> Cases => Suite.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        var result = Suite.Run(id);
        if (result.Skipped) Assert.Skip(result.Summary);
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void EveryCaseRunsOncePerTarget()
    {
        Assert.Equal(4 * Suite.Project.Tests.Count, Suite.Ids.Count);
        Assert.Equal("handshake/status [java-stream]", Suite.Ids.Single(id => id.StartsWith("handshake/status [java", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => Suite.Run("handshake/nonexistent"));
    }
}
