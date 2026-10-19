using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Cases written by the web UI (case page, Save as test): only their own text changes, with all their keys.</summary>
public class TestFileEditTests
{
    private const string Suite = """
        # Points.
        suite: points
        tags: [geometry]
        cases:
          # The origin.
          - {name: origin, type: Point, hex: 00 00}
          - name: one
            type: Point
            value: {x: 1, y: 1}   # both
            hex: 01 01
        """;

    private static TestCaseDef Read(string text, int index) => TestFiles.Load(text, "tests.yaml", new DiagnosticBag()).ToList()[index];

    [Fact]
    public void AddedCasesGoAfterTheLastOneAndKeepTheRest()
    {
        var added = TestFiles.Replace(Suite, null, new TestCaseDef
        {
            Name = "capture",
            Type = "Point",
            Hex = "02 03",
            Expect = new TestExpectation { HasValue = true, Value = new JsonObject { ["x"] = 2, ["y"] = 3 }, Consumed = 2 },
            Packet = new PacketFrame { Header = new PrimitiveRef(PrimitiveKind.U8) },
            Tags = ["geometry", "capture"],
        })!;

        Assert.StartsWith(Suite + "\n", added);
        Assert.EndsWith("""
              - name: capture
                type: Point
                hex: 02 03
                expect: {value: {x: 2, y: 3}, consumed: 2}
                packet: u8
                tags: [capture]
            """, added);
        var test = Read(added, 2);
        Assert.Equal(2, test.Expect!.Consumed);
        Assert.Equal(["geometry", "capture"], test.Tags);
    }

    [Fact]
    public void ReplacedCasesKeepTheirStyleAndWhatTheyDontChange()
    {
        var origin = Read(Suite, 0);
        var renamed = TestFiles.Replace(Suite, 0, new TestCaseDef { Name = "zero", Type = "Point", Hex = "00 00", Tags = origin.Tags, Entry = origin.Entry })!;
        Assert.Equal(Suite.Replace("name: origin", "name: zero"), renamed);

        // An expectation and a skip: the block entry is written again, the flow one above stays.
        var one = Read(Suite, 1);
        var edited = TestFiles.Replace(Suite, 1, new TestCaseDef
        {
            Name = "one", Type = "Point", Value = one.Value, Hex = one.Hex, Tags = one.Tags, Entry = one.Entry,
            Expect = new TestExpectation { Warnings = [] },
            Skip = "later",
        })!;
        Assert.Contains("  - {name: origin, type: Point, hex: 00 00}\n", edited);
        Assert.EndsWith("""
              - name: one
                type: Point
                value: {x: 1, y: 1}
                hex: 01 01
                expect: {warnings: []}
                skip: later
            """, edited);
        Assert.Equal("later", Read(edited, 1).Skip);
    }

    [Fact]
    public void HexThatLooksLikeANumberStaysHex()
    {
        foreach (var hex in new[] { "0102", "1e10", "00 01" })
        {
            var text = TestFiles.Replace(Suite, null, new TestCaseDef { Name = "n", Type = "Point", Hex = hex })!;
            Assert.Equal(hex, Read(text, 2).Hex);
            Assert.Equal(hex, Read(TestFiles.Replace(Suite, 0, new TestCaseDef { Name = "origin", Type = "Point", Hex = hex, Tags = ["geometry"] })!, 0).Hex);
        }
    }

    [Fact]
    public void DeletedCasesTakeTheirCommentsWithThem()
    {
        var deleted = TestFiles.Replace(Suite, 0, null)!;
        Assert.DoesNotContain("origin", deleted);
        Assert.Contains("cases:\n  - name: one\n", deleted);
        Assert.StartsWith("# Points.\n", deleted);
    }

    [Fact]
    public void ByteDifferencesNameTheFieldTheyAreIn()
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), "struct: Point\nfields:\n  - x: u8\n  - y: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Var.yaml"), "struct: Var\nfields:\n  - v: varint32\n");
        File.WriteAllText(Path.Combine(directory, "tests", "tests.yaml"), """
            - {name: stale, type: Point, value: {x: 1, y: 3}, hex: 01 02}
            - {name: long-varint, type: Var, hex: 8000}
            """);
        var results = TestRunner.Run(FormatProject.Load(directory)).ToDictionary(r => r.Case.Name);

        var stale = Assert.Single(results["stale"].Failures);
        Assert.Equal((1, "y"), (stale.Offset, stale.Path));

        var varint = Assert.Single(results["long-varint"].Failures);
        Assert.Equal(CaseFailureKind.RoundTrip, varint.Kind);
        Assert.Equal(("8000", "00", "v"), (Hex.Format(varint.Expected!), Hex.Format(varint.Actual!), varint.Path));
    }
}
