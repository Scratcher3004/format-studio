using System.Text;
using System.Text.Json.Nodes;
using FormatStudio.Codegen.CSharp;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen;

public sealed record GeneratedFile(string Path, string Content);

public sealed class GeneratedOutput
{
    public List<GeneratedFile> Files { get; } = [];
    public DiagnosticBag Diagnostics { get; } = [];
}

public interface ICodeGenerator
{
    string Name { get; }
    /// <summary>The first line of every generated file; only files starting with it are overwritten or deleted.</summary>
    string Marker { get; }
    GeneratedOutput Generate(FormatProject project, JsonObject options);
}

public sealed class WriteSummary
{
    public string OutputDirectory { get; init; } = "";
    public List<string> Written { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> Refused { get; } = [];
    public int Unchanged { get; set; }
}

/// <summary>
/// Runs code generators and writes their output. Generated files only go to the target's output folder inside the
/// project; existing files are only replaced or deleted when they carry the generator's marker. Copying the output into
/// a source tree is a manual, reviewed step.
/// </summary>
public static class CodeGeneration
{
    public static IReadOnlyList<ICodeGenerator> Generators { get; } = [new CSharpGenerator()];

    public static ICodeGenerator Find(string name) =>
        Generators.FirstOrDefault(g => g.Name == name)
        ?? throw new FormatDefinitionException($"Unknown generator '{name}' ({string.Join(", ", Generators.Select(g => g.Name))})");

    public static GeneratedOutput Generate(FormatProject project, CodegenTarget target) =>
        Find(target.Generator).Generate(project, target.Options);

    public static WriteSummary Write(FormatProject project, CodegenTarget target, GeneratedOutput output, bool clean = true)
    {
        var generator = Find(target.Generator);
        var directory = project.Sandbox.Check(Path.GetFullPath(target.Output, project.Directory));
        var projectDirectory = Path.GetFullPath(project.Directory) + Path.DirectorySeparatorChar;
        if (!(directory + Path.DirectorySeparatorChar).StartsWith(projectDirectory, StringComparison.Ordinal))
            throw new FormatDefinitionException($"The output folder {directory} is outside the project folder {project.Directory}");

        var summary = new WriteSummary { OutputDirectory = directory };
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in output.Files)
        {
            var path = project.Sandbox.Check(Path.Combine(directory, file.Path));
            written.Add(path);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (existing == file.Content)
                {
                    summary.Unchanged++;
                    continue;
                }

                if (!existing.StartsWith(generator.Marker, StringComparison.Ordinal))
                {
                    summary.Refused.Add(path);
                    continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, new UTF8Encoding(false));
            summary.Written.Add(path);
        }

        if (clean && Directory.Exists(directory))
        {
            foreach (var path in project.Sandbox.EnumerateFiles(directory, recursive: true).ToList())
            {
                if (written.Contains(Path.GetFullPath(path))) continue;
                using (var reader = new StreamReader(path))
                {
                    var buffer = new char[generator.Marker.Length];
                    if (reader.ReadBlock(buffer) != buffer.Length || new string(buffer) != generator.Marker) continue;
                }

                File.Delete(path);
                summary.Deleted.Add(path);
            }
        }

        return summary;
    }
}

/// <summary>Builds indented source text.</summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0) _builder.Append(' ', _indent * 4).Append(text);
        _builder.Append('\n');
        return this;
    }

    public CodeWriter Open(string text)
    {
        Line(text);
        Line("{");
        _indent++;
        return this;
    }

    public CodeWriter Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
        return this;
    }

    public void Indent() => _indent++;
    public void Outdent() => _indent--;

    public override string ToString() => _builder.ToString();
}
