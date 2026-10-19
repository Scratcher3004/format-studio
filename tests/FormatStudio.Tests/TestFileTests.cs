using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

/// <summary>Adding, changing and deleting test cases in their files.</summary>
public class TestFileTests
{
    private static FormatProject NewProject()
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), """
            struct: Point
            fields:
              - x: u8
              - y: u8
            """);
        return FormatProject.Load(directory);
    }

    private static TestCaseDef Case(string name, int x, string? hex = null) => new()
    {
        Name = name,
        Type = "Point",
        Value = new JsonObject { ["x"] = x, ["y"] = 2 },
        Hex = hex,
    };

    [Fact]
    public void CasesAreAddedChangedAndDeleted()
    {
        var project = NewProject();
        Assert.Equal("tests/tests.yaml", project.DefaultTestFile());

        project.SaveTest(Case("a", 1, "01 02"), "tests/tests.yaml");
        project = FormatProject.Load(project.FilePath!);
        project.SaveTest(Case("b", 3), "tests/tests.yaml");
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["a", "b"], project.Tests.Select(s => s.Name));
        Assert.Equal("01 02", project.Tests[0].Hex);
        Assert.Equal(1, project.Tests[1].Index);

        project.SaveTest(Case("b2", 4, "04 02"), "tests/tests.yaml", project.Tests[1]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["a", "b2"], project.Tests.Select(s => s.Name));
        Assert.Equal(4, project.Tests[1].Value!["x"]!.GetValue<long>());

        project.DeleteTest(project.Tests[0]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["b2"], project.Tests.Select(s => s.Name));
        Assert.Equal(0, project.Tests[0].Index);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Error);

        project.DeleteTest(project.Tests[0]);
        Assert.False(File.Exists(Path.Combine(project.Directory, "tests", "tests.yaml")));
    }

    [Fact]
    public void CasesMoveBetweenFiles()
    {
        var project = NewProject();
        project.SaveTest(Case("a", 1), "tests/one.yaml");
        project.SaveTest(Case("b", 2), "tests/one.yaml");
        project = FormatProject.Load(project.FilePath!);

        project.SaveTest(Case("a", 1), "tests/two.yaml", project.Tests[0]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal([("tests/one.yaml", "b"), ("tests/two.yaml", "a")], project.Tests.Select(s => (s.Source!.File, s.Name)));
    }

    [Fact]
    public void ReplacingKeepsTheShapeAndOtherKeysOfTheFile()
    {
        const string list = """
            - name: first
              type: Point
              note: kept
              value: {x: 1, y: 2}
            - name: second
              type: Point
              hex: "0102"
            """;
        var replaced = TestFiles.Replace(list, 0, Case("renamed", 5))!;
        var entries = TestFiles.Entries(Dsl.YamlValues.ParseYaml(replaced));
        Assert.IsType<JsonArray>(Dsl.YamlValues.ParseYaml(replaced));
        Assert.Equal("renamed", entries[0]["name"]!.ToString());
        Assert.Equal("kept", entries[0]["note"]!.ToString());
        Assert.Equal("0102", entries[1]["hex"]!.ToString());

        const string single = "sample: only\ntype: Point\nhex: 01 02\n";
        Assert.Equal("only", TestFiles.Entries(Dsl.YamlValues.ParseYaml(TestFiles.Replace(single, 0, Case("only", 1))!))[0]["sample"]!.ToString());
        var added = Dsl.YamlValues.ParseYaml(TestFiles.Replace(single, null, Case("more", 1))!);
        Assert.Equal(2, ((JsonArray)added!["cases"]!).Count);
        Assert.Null(TestFiles.Replace(single, 0, null));
    }

    [Fact]
    public void CasesOutsideTheTestPatternsAreRejected()
    {
        var project = NewProject();
        var error = Assert.Throws<FormatDefinitionException>(() => project.SaveTest(Case("a", 1), "types/a.yaml"));
        Assert.Contains("test patterns", error.Message);
        Assert.Throws<FormatDefinitionException>(() => project.SaveTest(Case("a", 1), "tests/a.txt"));
    }

    [Fact]
    public void ChangedFilesAreNotOverwritten()
    {
        var project = NewProject();
        project.SaveTest(Case("a", 1), "tests/tests.yaml");
        project = FormatProject.Load(project.FilePath!);
        File.WriteAllText(Path.Combine(project.Directory, "tests", "tests.yaml"), "samples:\n  - sample: other\n    type: Point\n    hex: 0102\n");
        Assert.Throws<FormatDefinitionException>(() => project.DeleteTest(project.Tests[0]));
    }
}
