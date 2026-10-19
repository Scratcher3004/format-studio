using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Web;

namespace FormatStudio.Tests;

/// <summary>Saving the general settings of a project file, as the web UI's settings page does.</summary>
public class ProjectSettingsTests
{
    private const string Before = """
        # My project
        name: p
        description: old
        defaults:
          endian: little   # dropped: the section is rewritten
          length: varuint32
        sources:
          - types/**/*.yaml

        # Packet id spaces
        protocols:
          - name: Login
            groups: [login]
        codegen:
          - name: cs
            generator: csharp
            output: out   # kept
        """;

    private static (string Directory, string File) Write(string text)
    {
        var directory = TestPaths.NewTempDirectory();
        var file = Path.Combine(directory, "p.fstudio.yaml");
        File.WriteAllText(file, text);
        return (directory, file);
    }

    [Fact]
    public void SavingSettingsKeepsTheOtherSections()
    {
        var (directory, file) = Write(Before);
        var project = FormatProject.Load(directory);
        var settings = ProjectSettings.Parse(project.Settings.ToYaml(), file);
        settings.Name = "geo";
        settings.Description = null;
        settings.Kind = ProjectKind.Library;
        settings.Version = "1.2.0";
        settings.Defaults.Endian = Endian.Big;
        settings.Defaults.Inherit.Add("endian");
        settings.Dependencies.Add(new LibraryDependency { Library = "units", Alias = "u", SkipCodegen = true });
        settings.Samples = ["samples/*.yaml"];

        project.SaveSettings(settings);
        var text = File.ReadAllText(file);
        Assert.StartsWith("# My project\nname: geo\nversion: 1.2.0\nkind: library\ndefaults:\n  endian: big\n", text);
        Assert.DoesNotContain("description", text);
        Assert.DoesNotContain("dropped", text);
        Assert.Contains("\n# Packet id spaces\nprotocols:\n", text);
        Assert.Contains("output: out   # kept", text);

        var reloaded = FormatProject.Load(directory).Settings;
        Assert.Equal("geo", reloaded.Name);
        Assert.Equal(ProjectKind.Library, reloaded.Kind);
        Assert.Equal("1.2.0", reloaded.Version);
        Assert.Equal(Endian.Big, reloaded.Defaults.Endian);
        Assert.True(reloaded.Defaults.Inherits("endian"));
        Assert.Equal("u", reloaded.Dependencies.Single().Alias);
        Assert.True(reloaded.Dependencies.Single().SkipCodegen);
        Assert.Equal(["samples/*.yaml"], reloaded.Samples);
        Assert.Equal("Login", reloaded.Protocols.Single().Name);
        Assert.Equal("cs", reloaded.Codegen.Single().Name);
    }

    [Fact]
    public void SavingSettingsRejectsMissingParts()
    {
        var (directory, file) = Write(Before);
        var project = FormatProject.Load(directory);
        var settings = ProjectSettings.Parse(project.Settings.ToYaml(), file);
        settings.Sources = [];
        Assert.Throws<FormatDefinitionException>(() => project.SaveSettings(settings));
        settings.Sources = ["**/*.yaml"];
        settings.Dependencies.Add(new LibraryDependency { Library = " " });
        Assert.Throws<FormatDefinitionException>(() => project.SaveSettings(settings));
        Assert.Equal(Before, File.ReadAllText(file));
    }

    [Fact]
    public void ServiceReloadsAfterSavingSettings()
    {
        var (_, file) = Write(Before);
        var service = new ProjectService(file);
        var settings = ProjectSettings.Parse(service.Project!.Settings.ToYaml(), file);
        settings.Defaults.Length = new PrimitiveRef(PrimitiveKind.U16);

        service.SaveSettings(settings);
        Assert.Equal(PrimitiveKind.U16, service.Project!.Settings.Defaults.Length.Kind);
        Assert.Null(service.LoadError);
    }
}
