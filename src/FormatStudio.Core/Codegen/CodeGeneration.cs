using System.Text;
using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen;

/// <param name="TypeName">The definition the file was generated from, or null for shared files (registry, runtime).</param>
public sealed record GeneratedFile(string Path, string Content, string? TypeName = null);

public sealed class GeneratedOutput
{
    public List<GeneratedFile> Files { get; } = [];
    public DiagnosticBag Diagnostics { get; } = [];

    /// <summary>The file generated from a definition, or null (aliases are inlined, definitions with skipCodegen are skipped).</summary>
    public GeneratedFile? FileOf(string typeName) => Files.FirstOrDefault(f => f.TypeName == typeName);
}

/// <summary>A language (and I/O model) code can be generated for.</summary>
public interface ICodeGenerator
{
    /// <summary>The <c>generator</c> of codegen targets, e.g. <c>csharp</c>.</summary>
    string Name { get; }
    /// <summary>Shown in the UI, e.g. "C# (spans)".</summary>
    string Title { get; }
    /// <summary>The language of the output, e.g. for syntax highlighting: <c>csharp</c>, <c>java</c>.</summary>
    string Language { get; }
    string Description { get; }
    /// <summary>The output folder of new targets, relative to the project.</summary>
    string DefaultOutput { get; }
    /// <summary>The first line of every generated file; only files starting with it are overwritten or deleted.</summary>
    string Marker { get; }
    IReadOnlyList<CodegenOption> Options { get; }

    /// <param name="skipped">Definitions that are not generated but referenced (see <see cref="TypeDef.SkipCodegen"/>).</param>
    GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped);
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
    /// <summary>A target with the generator's defaults, e.g. to preview code of a project without targets.</summary>
    public static CodegenTarget DefaultTarget(ICodeGenerator generator) =>
        new() { Name = generator.Name, Generator = generator.Name, Output = generator.DefaultOutput };

    /// <summary>
    /// The project's codegen target with this name, else its first one with this generator, else a target with the
    /// generator's defaults; null when there is neither.
    /// </summary>
    public static CodegenTarget? FindTarget(FormatProject project, string name)
    {
        var targets = project.Settings.Codegen;
        return targets.FirstOrDefault(t => t.Name == name) ?? targets.FirstOrDefault(t => t.Generator == name)
            ?? (project.Generators.Find(name) is { } generator ? DefaultTarget(generator) : null);
    }

    public static GeneratedOutput Generate(FormatProject project, CodegenTarget target)
    {
        var generator = project.Generators.Get(target.Generator);
        GeneratedOutput output;
        try
        {
            output = generator.Generate(project, target.Options, Excluded(project, target));
        }
        catch (FormatDefinitionException e)
        {
            // An invalid option value.
            output = new GeneratedOutput();
            output.Diagnostics.Error($"Target '{target.Name}': {e.Message}");
        }

        // A generator without its plugin knows no options; that it can't generate is reported already.
        if (generator is DeclaredGenerator { Implementation: null }) return output;
        foreach (var key in target.Options.Select(o => o.Key).Where(key => generator.Options.All(o => o.Key != key)))
            output.Diagnostics.Warning(key == "exclude"
                ? $"Target '{target.Name}': the option 'exclude' was replaced by 'skipCodegen' in the definitions; it is ignored"
                : $"Target '{target.Name}': unknown option '{key}' for {generator.Name}");
        return output;
    }

    /// <summary>The definitions a target doesn't generate: those whose <c>skipCodegen</c> names it, its generator, or all targets.</summary>
    public static IReadOnlySet<string> Skipped(FormatProject project, CodegenTarget target) =>
        project.Types.Values.Where(d => d.SkipCodegen?.Skips(target.Name, target.Generator) == true).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The definitions a target references but doesn't generate, by qualified name: the <see cref="Skipped"/> ones and
    /// those of libraries whose dependency says <c>codegen: {skip: true}</c>.
    /// </summary>
    public static IReadOnlySet<string> Excluded(FormatProject project, CodegenTarget target)
    {
        var excluded = Skipped(project, target).ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in project.Settings.Dependencies.Where(d => d.SkipCodegen))
        {
            if (project.Modules.TryGetValue(dependency.Library, out var module))
                excluded.UnionWith(module.Types.Values.Select(d => d.QualifiedName));
        }

        return excluded;
    }

    /// <summary>Checks a target before it is saved: the project has its generator and the output folder stays inside the project.</summary>
    public static void Check(FormatProject project, CodegenTarget target)
    {
        project.Generators.Get(target.Generator);
        if (string.IsNullOrWhiteSpace(target.Name)) throw new FormatDefinitionException("A codegen target needs a name");
        CheckOutput(target.Output);
    }

    /// <summary>Checks an output folder: relative, inside the project folder and not the project folder itself.</summary>
    public static void CheckOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new FormatDefinitionException("A codegen target needs an output folder");
        if (Path.IsPathRooted(output)) throw new FormatDefinitionException("The output folder must be relative to the project folder");
        var parts = output.Split('/', '\\');
        var depth = 0;
        foreach (var part in parts)
        {
            depth += part switch { ".." => -1, "." or "" => 0, _ => 1 };
            if (depth < 0) throw new FormatDefinitionException($"The output folder {output} is outside the project folder");
        }

        if (depth == 0) throw new FormatDefinitionException("The output folder can't be the project folder itself");
    }

    public static WriteSummary Write(FormatProject project, CodegenTarget target, GeneratedOutput output, bool clean = true)
    {
        var generator = project.Generators.Get(target.Generator);
        var marker = generator.Marker;
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

                if (!existing.StartsWith(marker, StringComparison.Ordinal))
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
                    var buffer = new char[marker.Length];
                    if (reader.ReadBlock(buffer) != buffer.Length || new string(buffer) != marker) continue;
                }

                File.Delete(path);
                summary.Deleted.Add(path);
            }
        }

        return summary;
    }
}
