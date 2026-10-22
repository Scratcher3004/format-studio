using FormatStudio.Minecraft;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Tests;

public class MinecraftLibraryTests
{
    private static FormatProject Load(string settings, string field)
    {
        var directory = TestPaths.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "p.fstudio.yaml"), settings + "sources: [types/*.yaml]\n");
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        File.WriteAllText(Path.Combine(directory, "types", "A.yaml"), $"struct: A\nfields:\n  - {field}\n");
        return FormatProject.Load(directory);
    }

    private static bool HasMinecraft(FormatProject project) => project.Modules.ContainsKey(MinecraftLibrary.Name);

    [Fact]
    public void ADeclaredDependencyProvidesTheNativeTypes()
    {
        var project = Load("name: p\ndefaults:\n  nbt: java\ndependencies: [minecraft]\n", "tag: nbt");
        Assert.Empty(project.Diagnostics.Where(d => d.Severity == Severity.Error));
        Assert.True(HasMinecraft(project));
        Assert.NotNull(project.Natives.Find("nbt"));
        Assert.Equal(MinecraftLibrary.Name, project.Settings.Dependencies.Single().Library);
        Assert.Equal(MinecraftLibrary.Name, HostLibraries.Of(project.Natives.Find("nbt")!)?.Name);
    }

    [Fact]
    public void WithoutTheDependencyNothingIsProvided()
    {
        var project = Load("name: p\n", "x: u8");
        Assert.False(HasMinecraft(project));
        Assert.Null(project.Natives.Find("nbt"));
    }

    // TODO(legacy-nbt): remove with HostLibraries.WithLegacyDependencies.
    [Theory]
    [InlineData("name: p\ndefaults:\n  nbt: java\n", "x: u8")]
    [InlineData("name: p\n", "tag: nbt(java)")]
    [InlineData("name: p\n", "tag: {nbt: java}")]
    public void UsingNbtAddsTheDependency(string settings, string field)
    {
        var project = Load(settings, field);
        Assert.Empty(project.Diagnostics.Where(d => d.Severity == Severity.Error));
        Assert.True(HasMinecraft(project));
        Assert.NotNull(project.Natives.Find("nbt"));
        Assert.Empty(project.Settings.Dependencies);
    }

    [Fact]
    public void AnUnknownNativeNameIsAnError()
    {
        var project = Load("name: p\n", "tag: {nbtx: java}");
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("nbtx"));
    }

    [Fact]
    public void AnUnknownLibraryIsAnError()
    {
        var project = Load("name: p\ndependencies: [minecraftx]\n", "x: u8");
        Assert.Contains(project.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("minecraftx"));
    }

    [Theory]
    [InlineData("csharp", "Natives/NbtCodec.cs")]
    [InlineData("csharp-stream", "Natives/NbtStreamCodec.cs")]
    [InlineData("java-stream", "natives/NbtCodec.java")]
    public void GeneratedCodeCarriesTheNbtCode(string generator, string file)
    {
        var project = Load("name: p\ndefaults:\n  nbt: java\ndependencies: [minecraft]\n", "tag: nbt");
        var output = Codegen.CodeGeneration.Generate(project, new CodegenTarget { Name = "t", Generator = generator, Output = "out" });
        Assert.Contains(output.Files, f => f.Path.EndsWith(file, StringComparison.Ordinal));
        Assert.DoesNotContain(output.Files.Where(f => f.Path.Contains("Runtime") || f.Path.Contains("runtime")),
            f => f.Content.Contains("ReadNbt") || f.Content.Contains("readNbt"));
    }
}
