using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Accepting what failed cases give: <c>test --update</c>, <c>auto</c> placeholders and in-place file edits.</summary>
public class TestSnapshotTests
{
    /// <summary>
    /// <c>Point {x: u8, y: u8}</c>, <c>Tagged {kind: u8, label: string}</c> and packet <c>A {a: u8}</c> (id 1) in
    /// protocol X, with the given test file.
    /// </summary>
    private static FormatProject Project(string tests)
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.AppendAllText(Path.Combine(directory, "test" + ProjectSettings.Extension), "protocols:\n  - name: X\n    groups: [x]\n");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), "struct: Point\nfields:\n  - x: u8\n  - y: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Tagged.yaml"), "struct: Tagged\nfields:\n  - kind: u8\n  - label: string\n");
        File.WriteAllText(Path.Combine(directory, "packets", "A.yaml"), "packet: A\nid: 1\ngroups: [x]\nfields:\n  - a: u8\n");
        File.WriteAllText(Path.Combine(directory, "tests", "tests.yaml"), tests);
        return FormatProject.Load(directory);
    }

    private static string TestFile(FormatProject project) => File.ReadAllText(Path.Combine(project.Directory, "tests", "tests.yaml"));

    [Fact]
    public void UpdatesReplaceValuesWhereTheyStandAndKeepComments()
    {
        const string text = """
            # Points.
            - name: a
              hex: 01 02   # the bytes
              expect: {value: {x: 1}, match: subset}
            - name: b
              expect:
                value:
                  x: 1
                # after the value
                warnings: [old]
                consumed: 9
            - name: c
              expect:
                value: auto
            """;

        var a = TestFiles.Update(text, 0, new TestUpdate { Hex = "0304", SetsValue = true, Value = new System.Text.Json.Nodes.JsonObject { ["x"] = 3 } });
        Assert.Contains("  hex: 0304   # the bytes\n", a);
        Assert.Contains("  expect: {value: {x: 3}, match: subset}\n", a);
        Assert.StartsWith("# Points.\n", a);

        var b = TestFiles.Update(text, 1, new TestUpdate
        {
            SetsValue = true,
            Value = new System.Text.Json.Nodes.JsonObject { ["x"] = 5, ["y"] = 6 },
            Warnings = ["new one"],
            Consumed = 2,
        });
        Assert.Contains("    value:\n      x: 5\n      y: 6\n    # after the value\n    warnings: [new one]\n    consumed: 2\n", b);

        // A value too long for the key's line goes below it as a block.
        var label = new string('z', 120);
        var c = TestFiles.Update(text, 2, new TestUpdate { SetsValue = true, Value = new System.Text.Json.Nodes.JsonObject { ["kind"] = 1, ["label"] = label } });
        Assert.EndsWith($"    value:\n      kind: 1\n      label: {label}", c);
    }

    [Fact]
    public void UpdatesKeepWindowsLineEndings()
    {
        const string text = "- name: a\r\n  expect:\r\n    value:\r\n      x: 1\r\n";
        var updated = TestFiles.Update(text, 0, new TestUpdate { SetsValue = true, Value = new System.Text.Json.Nodes.JsonObject { ["x"] = 2, ["y"] = 3 } });
        Assert.Equal("- name: a\r\n  expect:\r\n    value:\r\n      x: 2\r\n      y: 3\r\n", updated);
    }

    [Fact]
    public void AutoPlaceholdersFailUntilTheyAreFilledIn()
    {
        var project = Project("""
            - {name: hex, type: Point, value: {x: 1, y: 2}, hex: auto}
            - {name: value, type: Point, hex: 01 02, expect: {value: auto}}
            - {name: no-value, type: Point, hex: auto}
            """);
        Assert.Contains(project.Diagnostics, d => d.Message == "Test no-value: 'hex: auto' is filled in from the value's encoding, so the case needs a 'value'");

        var results = TestRunner.Run(project).ToDictionary(r => r.Case.Name);
        Assert.Equal(CaseFailureKind.Snapshot, Assert.Single(results["hex"].Failures).Kind);
        Assert.Equal("the expected value is auto; format-studio test --update fills it in", results["value"].Summary);
    }

    [Fact]
    public void UpdateAllAcceptsWhatTheCasesGiveAndLeavesTheRest()
    {
        var project = Project("""
            # Kept.
            suite: points
            cases:
              - {name: auto-hex, type: Point, value: {x: 1, y: 2}, hex: auto}
              - {name: stale, type: Point, value: {x: 1, y: 3}, hex: 01 02}
              - {name: value, type: Tagged, hex: 01 02 6869, expect: {value: auto}}
              - {name: subset, type: Tagged, hex: 01 02 6869, expect: {value: {label: no}, match: subset}}
              - {name: framed, type: A, packet: {header: u8, protocol: X}, value: {a: 7}, hex: auto}
              - {name: truncated, type: Tagged, hex: 01 05 61}
            """);
        var outcomes = TestSnapshots.UpdateAll(project, () => FormatProject.Load(project.FilePath!), null, out var updated)
            .ToDictionary(o => o.Case.Name);

        Assert.Equal(["hex"], outcomes["auto-hex"].Updated);
        Assert.Equal(["expect.value"], outcomes["value"].Updated);
        Assert.Empty(outcomes["truncated"].Updated);
        Assert.Contains("remaining", outcomes["truncated"].Reason);
        Assert.Equal(["truncated"], TestRunner.Run(updated).Where(r => r.Failed).Select(r => r.Case.Name));

        var text = TestFile(updated);
        Assert.StartsWith("# Kept.\n", text);
        // A space between the fields, and after a packet id.
        Assert.Contains("hex: 01 02}", text);
        Assert.Contains("hex: 01 03}", text);
        Assert.Contains("expect: {value: {kind: 1, label: hi}}", text);
        Assert.Contains("expect: {value: {label: hi}, match: subset}", text);
        Assert.Contains("value: {a: 7}, hex: 01 07}", text);

        // Nothing is left to accept.
        Assert.DoesNotContain(TestSnapshots.UpdateAll(updated, () => FormatProject.Load(project.FilePath!), null, out _), o => o.Updated.Count > 0);
    }

    [Fact]
    public void BytesFromFilesAndBrokenCasesAreNotAccepted()
    {
        var project = Project("""
            - {name: from-file, type: Point, value: {x: 9, y: 9}, file: point.bin}
            - {name: too-big, type: Point, value: {x: 300, y: 1}, hex: 01 02}
            - {name: passes, type: Point, hex: 01 02}
            """);
        File.WriteAllBytes(Path.Combine(project.Directory, "tests", "point.bin"), [1, 2]);
        project = FormatProject.Load(project.FilePath!);
        var results = TestRunner.Run(project).ToDictionary(r => r.Case.Name);

        Assert.Equal("its bytes come from point.bin; change the file or the value", TestSnapshots.Accept(project, results["from-file"]).Reason);
        Assert.Null(TestSnapshots.Accept(project, results["too-big"]).Update);
        Assert.Equal("it passes", TestSnapshots.Accept(project, results["passes"]).Reason);
    }
}
