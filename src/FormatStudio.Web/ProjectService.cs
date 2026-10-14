using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Importers;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using FormatStudio.Validation;

namespace FormatStudio.Web;

/// <summary>The project shown by the web UI. Reloaded from disk after every change.</summary>
public sealed class ProjectService
{
    private readonly Lock _lock = new();
    private GeneratedOutput? _generated;

    public ProjectService(string path, FileSandbox? sandbox = null)
    {
        Path = path;
        Sandbox = sandbox ?? FileSandbox.Unrestricted;
        Reload();
    }

    public string Path { get; }
    /// <summary>The workspace; the project's files are only read and written inside it.</summary>
    public FileSandbox Sandbox { get; }
    public FormatProject? Project { get; private set; }
    public string? LoadError { get; private set; }
    /// <summary>Increased on every reload, so that pages can drop cached results.</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    public void Reload()
    {
        lock (_lock)
        {
            try
            {
                Project = FormatProject.Load(Path, Sandbox);
                LoadError = null;
            }
            catch (Exception e) when (e is FormatDefinitionException or IOException or System.FormatException or YamlDotNet.Core.YamlException)
            {
                LoadError = e.Message;
            }

            _generated = null;
            Version++;
        }

        Changed?.Invoke();
    }

    /// <summary>The text of a definition's file, or the DSL of definitions without a file.</summary>
    public string Text(TypeDef definition)
    {
        var file = Sandbox.Check(Project!.FileFor(definition));
        return File.Exists(file) ? File.ReadAllText(file) : DslWriter.Write(definition);
    }

    public bool IsImported(TypeDef definition)
    {
        var file = Project!.FileFor(definition);
        return Sandbox.Contains(file) && ImporterRegistry.IsImported(file);
    }

    /// <summary>Parses and validates edited text against the rest of the project.</summary>
    public (TypeDef? Definition, DiagnosticBag Diagnostics) Check(string text, string fileName)
    {
        var diagnostics = new DiagnosticBag();
        var definition = DslReader.Read(text, fileName, diagnostics);
        if (definition != null && Project != null) diagnostics.AddRange(Validator.Validate(Project, definition));
        return (definition, diagnostics);
    }

    /// <summary>Decodes bytes as a definition that is still being edited, with the rest of the project as it is.</summary>
    public DecodeResult Preview(TypeDef draft, byte[] bytes) =>
        new Codec(Project!.With(draft)).Decode(new NamedRef(draft.Name), bytes);

    /// <summary>Writes the text to the definition's file (or to <paramref name="file"/>) and reloads.</summary>
    public string Save(TypeDef original, string text, string? file = null)
    {
        file = Sandbox.Check(file ?? Project!.FileFor(original));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        Reload();
        return file;
    }

    /// <summary>
    /// Where an override of an imported definition goes: the folder of the last source pattern
    /// (e.g. <c>overrides/**/*.yaml</c> gives <c>overrides/&lt;kind&gt;s/Name.yaml</c>), or null if there is none.
    /// </summary>
    public string? OverrideFile(TypeDef definition)
    {
        var pattern = Project!.Settings.Sources.LastOrDefault(s => !s.StartsWith('!'));
        if (pattern == null) return null;
        var wildcard = pattern.IndexOfAny(['*', '?', '[']);
        var folder = wildcard < 0 ? System.IO.Path.GetDirectoryName(pattern) ?? "" : pattern[..wildcard].TrimEnd('/');
        if (folder.Length == 0) return null;
        var overrideFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Project.Directory, folder)) + System.IO.Path.DirectorySeparatorChar;
        if (System.IO.Path.GetFullPath(Project.FileFor(definition)).StartsWith(overrideFolder, StringComparison.Ordinal)) return null;
        var kindFolder = definition.Kind switch { "packet" => "packets", "struct" => "types", var kind => kind + "s" };
        return System.IO.Path.Combine(overrideFolder, kindFolder, definition.Name + ".yaml");
    }

    /// <summary>Definitions that use the given type.</summary>
    public IEnumerable<(TypeDef User, string Where)> References(string name)
    {
        if (Project == null) yield break;
        foreach (var definition in Project.Types.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            switch (definition)
            {
                case StructDef structDef:
                    foreach (var field in structDef.Fields.Where(f => Uses(f.Type, name))) yield return (definition, field.Name);
                    break;
                case VariantDef variant:
                    if (variant.Tag != null && Uses(variant.Tag, name)) yield return (definition, "tag");
                    foreach (var variantCase in variant.Cases.Where(c => c.Type != null && Uses(c.Type, name)))
                        yield return (definition, variantCase.Name);
                    break;
                case AliasDef alias when Uses(alias.Target, name):
                    yield return (definition, "type");
                    break;
                case BitfieldDef bitfield:
                    foreach (var member in bitfield.Members.Where(m => m.Type == name)) yield return (definition, member.Name);
                    break;
            }
        }
    }

    private static bool Uses(TypeRef type, string name) => type switch
    {
        NamedRef named => named.Name == name,
        ArrayRef array => Uses(array.Element, name),
        MapRef map => Uses(map.Key, name) || Uses(map.Value, name),
        OptionalRef optional => Uses(optional.Inner, name),
        SizedRef sized => Uses(sized.Inner, name),
        _ => false,
    };

    /// <summary>The output of the first codegen target, generated once per project version.</summary>
    public GeneratedOutput Generated(CodegenTarget target)
    {
        lock (_lock)
        {
            return _generated ??= CodeGeneration.Generate(Project!, target);
        }
    }

    public CodegenTarget DefaultTarget => Project?.Settings.Codegen.FirstOrDefault()
        ?? new CodegenTarget { Name = "csharp", Generator = "csharp", Output = "generated/csharp" };
}
