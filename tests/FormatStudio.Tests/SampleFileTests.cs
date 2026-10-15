using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

/// <summary>Adding, changing and deleting samples in their files.</summary>
public class SampleFileTests
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

    private static SampleDef Sample(string name, int x, string? hex = null) => new()
    {
        Name = name,
        Type = "Point",
        Value = new JsonObject { ["x"] = x, ["y"] = 2 },
        Hex = hex,
    };

    [Fact]
    public void SamplesAreAddedChangedAndDeleted()
    {
        var project = NewProject();
        Assert.Equal("samples/samples.yaml", project.DefaultSampleFile());

        project.SaveSample(Sample("a", 1, "01 02"), "samples/samples.yaml");
        project = FormatProject.Load(project.FilePath!);
        project.SaveSample(Sample("b", 3), "samples/samples.yaml");
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["a", "b"], project.Samples.Select(s => s.Name));
        Assert.Equal("01 02", project.Samples[0].Hex);
        Assert.Equal(1, project.Samples[1].Index);

        project.SaveSample(Sample("b2", 4, "04 02"), "samples/samples.yaml", project.Samples[1]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["a", "b2"], project.Samples.Select(s => s.Name));
        Assert.Equal(4, project.Samples[1].Value!["x"]!.GetValue<long>());

        project.DeleteSample(project.Samples[0]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal(["b2"], project.Samples.Select(s => s.Name));
        Assert.Equal(0, project.Samples[0].Index);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Error);

        project.DeleteSample(project.Samples[0]);
        Assert.False(File.Exists(Path.Combine(project.Directory, "samples", "samples.yaml")));
    }

    [Fact]
    public void SamplesMoveBetweenFiles()
    {
        var project = NewProject();
        project.SaveSample(Sample("a", 1), "samples/one.yaml");
        project.SaveSample(Sample("b", 2), "samples/one.yaml");
        project = FormatProject.Load(project.FilePath!);

        project.SaveSample(Sample("a", 1), "samples/two.yaml", project.Samples[0]);
        project = FormatProject.Load(project.FilePath!);
        Assert.Equal([("samples/one.yaml", "b"), ("samples/two.yaml", "a")], project.Samples.Select(s => (s.Source!.File, s.Name)));
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
        var replaced = SampleFiles.Replace(list, 0, Sample("renamed", 5))!;
        var entries = SampleFiles.Entries(Dsl.YamlValues.ParseYaml(replaced));
        Assert.IsType<JsonArray>(Dsl.YamlValues.ParseYaml(replaced));
        Assert.Equal("renamed", entries[0]["name"]!.ToString());
        Assert.Equal("kept", entries[0]["note"]!.ToString());
        Assert.Equal("0102", entries[1]["hex"]!.ToString());

        const string single = "sample: only\ntype: Point\nhex: 01 02\n";
        Assert.Equal("only", SampleFiles.Entries(Dsl.YamlValues.ParseYaml(SampleFiles.Replace(single, 0, Sample("only", 1))!))[0]["sample"]!.ToString());
        var added = Dsl.YamlValues.ParseYaml(SampleFiles.Replace(single, null, Sample("more", 1))!);
        Assert.Equal(2, ((JsonArray)added!["samples"]!).Count);
        Assert.Null(SampleFiles.Replace(single, 0, null));
    }

    [Fact]
    public void SamplesOutsideTheSamplePatternsAreRejected()
    {
        var project = NewProject();
        var error = Assert.Throws<FormatDefinitionException>(() => project.SaveSample(Sample("a", 1), "types/a.yaml"));
        Assert.Contains("sample patterns", error.Message);
        Assert.Throws<FormatDefinitionException>(() => project.SaveSample(Sample("a", 1), "samples/a.txt"));
    }

    [Fact]
    public void ChangedFilesAreNotOverwritten()
    {
        var project = NewProject();
        project.SaveSample(Sample("a", 1), "samples/samples.yaml");
        project = FormatProject.Load(project.FilePath!);
        File.WriteAllText(Path.Combine(project.Directory, "samples", "samples.yaml"), "samples:\n  - sample: other\n    type: Point\n    hex: 0102\n");
        Assert.Throws<FormatDefinitionException>(() => project.DeleteSample(project.Samples[0]));
    }
}
