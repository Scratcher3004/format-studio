using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Tests;

/// <summary>The projects in the repository: valid, samples with known encodings, random round trips.</summary>
public class ProjectTests
{
    public static TheoryData<string> Projects => [TestPaths.JavaSample];

    [Theory]
    [MemberData(nameof(Projects))]
    public void IsValid(string path)
    {
        var project = FormatProject.Load(path);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity != Severity.Info);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void SamplesEncodeAsExpected(string path)
    {
        var project = FormatProject.Load(path);
        var codec = new Codec(project);
        Assert.NotEmpty(project.Samples);
        foreach (var sample in project.Samples)
        {
            var type = Codec.ParseType(sample.Type);
            var encoded = codec.Encode(type, sample.Value);
            Assert.True(encoded.Success, $"{sample.Name}: {string.Join("\n", encoded.Diagnostics)}");
            if (sample.Hex != null) Assert.Equal(Hex.Format(Hex.Parse(sample.Hex)), Hex.Format(encoded.Bytes));
            var roundTrip = Simulator.RoundTripBytes(codec, type, encoded.Bytes);
            Assert.True(roundTrip.Success, $"{sample.Name}: {string.Join("\n", roundTrip.Diagnostics)}");
        }
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void RandomValuesRoundTrip(string path)
    {
        var project = FormatProject.Load(path);
        var failures = Simulator.Fuzz(project, project.Types.Values.OfType<StructDef>(), seed: 42, iterations: 5)
            .Where(r => !r.Success)
            .Select(r => $"{r.Type}: {string.Join("; ", r.Diagnostics.Where(d => d.Severity == Severity.Error))}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }
}
