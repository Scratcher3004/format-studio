using System.Text.Json.Nodes;
using System.Xml.Linq;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Expectations, suites, fixture files, filters and reports of test cases.</summary>
public class TestSuiteTests
{
    /// <summary>A project with <c>Point {x: u8, y: u8}</c>, <c>Tagged {kind: u8, label: string}</c> and the given test files.</summary>
    private static FormatProject Project(params (string File, string Text)[] files)
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), "struct: Point\nfields:\n  - x: u8\n  - y: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Tagged.yaml"), "struct: Tagged\nfields:\n  - kind: u8\n  - label: string\n");
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return FormatProject.Load(directory);
    }

    private static FormatProject Project(string tests) => Project(("tests/tests.yaml", tests));

    private static Dictionary<string, CaseResult> Results(FormatProject project, TestFilter? filter = null) =>
        TestRunner.Run(project, filter).ToDictionary(r => r.Case.Name);

    private static void AssertNoErrors(FormatProject project) =>
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Error);

    [Fact]
    public void DecodedValuesAreComparedExactlyOrAsASubset()
    {
        var project = Project("""
            - {name: exact, type: Point, hex: 01 02, expect: {value: {x: 1, y: 2}}}
            - {name: exact-missing-field, type: Point, hex: 01 02, expect: {value: {x: 1}}}
            - {name: subset, type: Point, hex: 01 02, expect: {value: {x: 1}, match: subset}}
            - {name: wrong, type: Point, hex: 01 02, expect: {value: {x: 9}, match: subset}}
            - {name: from-value, type: Tagged, value: {kind: 1, label: hi}, expect: {value: {kind: 1, label: hi}}}
            - {name: plain-hex, type: Point, hex: 0102, expect: {value: {x: 1, y: 2}}}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        Assert.True(results["exact"].Passed);
        Assert.True(results["subset"].Passed);
        Assert.True(results["from-value"].Passed);
        Assert.True(results["plain-hex"].Passed, results["plain-hex"].Summary);

        var extra = Assert.Single(results["exact-missing-field"].Failures);
        Assert.Equal(CaseFailureKind.Value, extra.Kind);
        Assert.Equal("y", extra.Path);
        Assert.Equal("not expected, decoded as 2", extra.Message);

        var wrong = Assert.Single(results["wrong"].Failures);
        Assert.Equal("x: expected 9, decoded 1", results["wrong"].Summary);
        Assert.Equal("x", wrong.Path);
    }

    [Fact]
    public void FailingCasesMustFailAsExpected()
    {
        var project = Project("""
            - {name: truncated, type: Tagged, hex: 01 05 61, expect: {error: remaining, path: label}}
            - {name: other-path, type: Tagged, hex: 01 05 61, expect: {path: kind}}
            - {name: decodes, type: Point, hex: 01 02, expect: {fails: decode}}
            - {name: too-big, type: Point, value: {x: 300, y: 1}, expect: {fails: encode}}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        Assert.True(results["truncated"].Passed, results["truncated"].Summary);
        Assert.True(results["too-big"].Passed, results["too-big"].Summary);
        Assert.Equal(CaseFailureKind.ExpectedFailure, results["other-path"].Failures.Single().Kind);
        Assert.StartsWith("fails with other errors than at kind: At offset 2:", results["other-path"].Summary);
        Assert.Equal("decodes without errors, but should fail", results["decodes"].Summary);
    }

    [Fact]
    public void WarningsAndConsumedBytesCanBeExpected()
    {
        var project = Project("""
            - {name: warned, type: Point, value: {x: 1, y: 2, z: 3}, expect: {warnings: ["unknown field 'z'"]}}
            - {name: no-warnings, type: Point, value: {x: 1, y: 2, z: 3}, expect: {warnings: []}}
            - {name: missing-warning, type: Point, value: {x: 1, y: 2}, expect: {warnings: [something]}}
            - {name: prefix, type: Point, hex: 01 02 ff ff, expect: {consumed: 2}}
            - {name: prefix-with-value, type: Point, value: {x: 1, y: 2}, hex: 01 02 ff, expect: {consumed: 2}}
            - {name: wrong-length, type: Point, hex: 01 02 ff, expect: {consumed: 3}}
            - {name: trailing, type: Point, hex: 01 02 ff}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        Assert.True(results["warned"].Passed, results["warned"].Summary);
        Assert.Equal("unexpected warning: Unknown field 'z' in Point", results["no-warnings"].Summary);
        Assert.Equal("no warning like 'something'", results["missing-warning"].Summary);
        Assert.True(results["prefix"].Passed, results["prefix"].Summary);
        Assert.True(results["prefix-with-value"].Passed, results["prefix-with-value"].Summary);
        Assert.Contains(results["wrong-length"].Failures, f => f.Kind == CaseFailureKind.Consumed);
        Assert.False(results["trailing"].Passed);
    }

    [Fact]
    public void SuitesGiveTheirCasesATypeTagsAndASkip()
    {
        var project = Project(
            ("tests/points.yaml", """
                suite: points
                type: Point
                tags: [geo]
                cases:
                  - {name: origin, hex: 00 00, tags: [fast]}
                  - {name: from-file, file: fixtures/point.bin}
                  - {name: tagged, type: Tagged, value: {kind: 1, label: a}}
                  - {name: later, hex: 00, skip: needs a 1-byte point}
                """),
            ("tests/fixtures/point.bin", "\u0003\u0004"),
            ("tests/old.yaml", "samples:\n  - sample: legacy\n    type: Point\n    hex: 05 06\n"));
        AssertNoErrors(project);

        var origin = project.Tests.Single(t => t.Name == "origin");
        Assert.Equal(("points", "Point"), (origin.Suite, origin.Type));
        Assert.Equal(["geo", "fast"], origin.Tags);
        Assert.Equal("Tagged", project.Tests.Single(t => t.Name == "tagged").Type);
        Assert.Equal("old", project.Tests.Single(t => t.Name == "legacy").Suite);

        var results = Results(project);
        Assert.True(results["origin"].Passed);
        Assert.True(results["from-file"].Passed, results["from-file"].Summary);
        Assert.Equal([3, 4], results["from-file"].Bytes);
        Assert.True(results["tagged"].Passed);
        Assert.Equal(CaseStatus.Skipped, results["later"].Status);
        Assert.True(results["legacy"].Passed);
    }

    [Fact]
    public void FiltersPickCasesByNameTagTypeAndFile()
    {
        var project = Project(
            ("tests/points.yaml", "suite: points\ntype: Point\ncases:\n  - {name: a, hex: 0000, tags: [fast]}\n  - {name: b, hex: 0101}\n"),
            ("tests/tagged.yaml", "- {name: a, type: Tagged, value: {kind: 1, label: x}}\n"));

        string[] Names(TestFilter filter) => TestRunner.Run(project, filter).Select(r => $"{r.Case.Suite}/{r.Case.Name}").ToArray();
        Assert.Equal(["points/a", "tagged/a"], Names(new TestFilter { Names = ["a"] }));
        Assert.Equal(["points/a", "points/b"], Names(new TestFilter { Names = ["points/*"] }));
        Assert.Equal(["points/a"], Names(new TestFilter { Tags = ["fast"] }));
        Assert.Equal(["tagged/a"], Names(new TestFilter { Types = ["Tagged"] }));
        Assert.Equal(["points/b"], Names(new TestFilter { Files = ["tests/p*.yaml"], Names = ["b"] }));
    }

    [Fact]
    public void ProblemsWithExpectationsAndFilesAreReportedWhenTheProjectLoads()
    {
        var project = Project("""
            - {name: both, type: Point, hex: 0102, file: p.bin}
            - {name: missing-file, type: Point, file: nowhere.bin}
            - {name: no-bytes, type: Point, value: {x: 1, y: 2}, expect: {fails: decode}}
            - {name: typo, type: Point, hex: 0102, expect: {valu: {x: 1}}}
            - {name: bad-match, type: Point, hex: 0102, expect: {value: {x: 1}, match: some}}
            - {name: skipped, type: NotYet, hex: 01, skip: true}
            """);
        string Find(string text) => Assert.Single(project.Diagnostics, d => d.Message.Contains(text)).Message;

        Find("both a 'hex' and a 'file'");
        Find("there is no file nowhere.bin");
        Find("'fails: decode' needs a 'hex' or 'file'");
        Assert.Contains("unknown key 'expect.valu'", Find("expect.valu"));
        Find("unknown match 'some' (exact, subset)");
        Assert.DoesNotContain(project.Diagnostics, d => d.Message.Contains("NotYet"));
        Assert.Equal("skipped", project.Tests.Single(t => t.Name == "skipped").Skip);
    }

    [Fact]
    public void ReportsListEveryCase()
    {
        var project = Project("""
            suite: points
            type: Point
            cases:
              - {name: ok, hex: 01 02}
              - {name: wrong, value: {x: 1, y: 2}, hex: 01 03}
              - {name: later, hex: 01, skip: not yet}
            """);
        var results = TestRunner.Run(project);

        var json = JsonNode.Parse(TestReports.Json("test", results))!;
        Assert.Equal((1, 1, 1), (json["passed"]!.GetValue<int>(), json["failed"]!.GetValue<int>(), json["skipped"]!.GetValue<int>()));
        var failure = json["cases"]![1]!["failures"]![0]!;
        Assert.Equal("mismatch", failure["kind"]!.GetValue<string>());
        Assert.Equal(1, failure["offset"]!.GetValue<int>());
        Assert.Equal("0103", failure["expected"]!.GetValue<string>());
        Assert.Equal("tests/tests.yaml", json["cases"]![0]!["file"]!.GetValue<string>());

        var junit = XDocument.Parse(TestReports.JUnit("test", results));
        var suite = junit.Root!.Element("testsuite")!;
        Assert.Equal(("points", "3", "1", "1"), (suite.Attribute("name")!.Value, suite.Attribute("tests")!.Value,
            suite.Attribute("failures")!.Value, suite.Attribute("skipped")!.Value));
        var cases = suite.Elements("testcase").ToList();
        Assert.Equal("test.points", cases[0].Attribute("classname")!.Value);
        Assert.Equal("Mismatch", cases[1].Element("failure")!.Attribute("type")!.Value);
        Assert.NotNull(cases[2].Element("skipped"));
    }

    [Fact]
    public void EditsKeepTheSuiteTypeAndTheOtherKeysOfACase()
    {
        const string suite = """
            suite: points
            type: Point
            cases:
              - name: a
                hex: 01 02
                expect: {value: {x: 1, y: 2}}
            """;
        var project = Project(("tests/one.yaml", suite));
        var original = project.Tests.Single();
        var edited = new TestCaseDef
        {
            Name = "a2", Type = "Point", Hex = "03 04", Expect = original.Expect, Entry = original.Entry, Source = original.Source,
        };

        var replaced = YamlValues.ParseYaml(TestFiles.Replace(suite, 0, edited)!)!;
        var entry = replaced["cases"]![0]!;
        Assert.Null(entry["type"]);
        Assert.Equal("a2", entry["name"]!.GetValue<string>());
        Assert.NotNull(entry["expect"]);

        // Moved to a file without a suite type, the case keeps its expectation and gets a type.
        project.SaveTest(edited, "tests/two.yaml", original);
        var moved = FormatProject.Load(project.FilePath!).Tests.Single();
        Assert.Equal(("tests/two.yaml", "Point"), (moved.Source!.File, moved.Type));
        Assert.True(moved.Expect!.HasValue);
    }
}
