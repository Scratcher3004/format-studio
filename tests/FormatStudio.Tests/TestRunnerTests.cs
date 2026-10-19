using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Running test cases, and the checks of test files when a project loads.</summary>
public class TestRunnerTests
{
    private static FormatProject Project(string tests)
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), """
            struct: Point
            fields:
              - x: u8
              - y: u8
            """);
        File.WriteAllText(Path.Combine(directory, "tests", "tests.yaml"), tests);
        return FormatProject.Load(directory);
    }

    private static CaseResult Result(FormatProject project, string name) => TestRunner.Run(project).Single(r => r.Case.Name == name);

    [Fact]
    public void CasesPassOrSayWhyTheyFail()
    {
        var project = Project("""
            - name: good
              type: Point
              value: {x: 1, y: 2}
              hex: 01 02
            - name: hex-only
              type: Point
              hex: 01 02
            - name: wrong
              type: Point
              value: {x: 1, y: 2}
              hex: 01 03
            - name: too-big
              type: Point
              value: {x: 300, y: 2}
            - name: trailing
              type: Point
              hex: 01 02 03
            """);

        Assert.True(Result(project, "good").Passed);
        Assert.Equal("ok", Result(project, "good").Summary);
        Assert.True(Result(project, "hex-only").Passed);

        var wrong = Assert.Single(Result(project, "wrong").Failures);
        Assert.Equal(CaseFailureKind.Mismatch, wrong.Kind);
        Assert.Equal(1, wrong.Offset);
        Assert.Equal([1, 3], wrong.Expected);
        Assert.Equal([1, 2], wrong.Actual);
        Assert.Equal("differs: 0102", Result(project, "wrong").Summary);

        var tooBig = Result(project, "too-big");
        Assert.Equal(CaseFailureKind.Encode, tooBig.Failures[0].Kind);
        Assert.StartsWith("encode failed: ", tooBig.Summary);

        Assert.Contains(Result(project, "trailing").Failures, f => f.Kind == CaseFailureKind.RoundTrip && f.Message.Contains("consumed 2 of 3"));
    }

    [Fact]
    public void CasesKnowTheirLines()
    {
        var project = Project("""
            samples:
              - sample: a
                type: Point
                hex: 01 02
              - sample: b
                type: Point
                hex: 03 04
            """);
        Assert.Equal([2, 5], project.Tests.Select(s => s.Source!.Line));
        Assert.Equal("tests/tests.yaml:5:5", project.Tests[1].Source!.ToString());
        Assert.Equal([0, 1], project.Tests.Select(s => s.Index));
    }

    [Fact]
    public void BrokenCasesAreReportedWhenTheProjectLoads()
    {
        var project = Project("""
            - name: unknown-type
              type: Pointy
              hex: 01 02
            - name: bad-hex
              type: Point
              hex: 0g
            - name: empty
              type: Point
            - name: typo
              type: Point
              hexx: 01 02
              hex: 01 02
            - name: no-type
              hex: 01 02
            - name: typo
              type: Point
              hex: 01 02
            """);
        string Find(string text) => Assert.Single(project.Diagnostics, d => d.Message.Contains(text)).ToString();

        Assert.StartsWith("tests/tests.yaml:1:3: error:", Find("Pointy"));
        Assert.StartsWith("tests/tests.yaml:4:3: error:", Find("invalid hex"));
        Assert.StartsWith("tests/tests.yaml:7:3: error:", Find("needs a 'value', 'hex' or 'file'"));
        Assert.StartsWith("tests/tests.yaml:9:3: warning:", Find("unknown key 'hexx'"));
        Assert.StartsWith("tests/tests.yaml:13:3: error:", Find("needs a 'type'"));
        Assert.StartsWith("tests/tests.yaml:15:3: warning:", Find("Several tests are named typo"));

        // The entry without a type is left out; the others stay, at the indices edits use.
        Assert.Equal(["unknown-type", "bad-hex", "empty", "typo", "typo"], project.Tests.Select(s => s.Name));
        Assert.Equal([0, 1, 2, 3, 5], project.Tests.Select(s => s.Index));
        Assert.All(TestRunner.Run(project).Skip(1).Take(2), r => Assert.Equal(CaseFailureKind.Invalid, r.Failures[0].Kind));
    }

    [Fact]
    public void YamlErrorsHaveALine()
    {
        var project = Project("- name: a\n  type: Point\n  hex: [01\n");
        var error = Assert.Single(project.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("tests/tests.yaml", error.Source!.File);
        Assert.True(error.Source.Line > 0);
    }
}
