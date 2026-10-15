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
    private readonly Dictionary<string, GeneratedOutput> _generated = new(StringComparer.Ordinal);

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

            _generated.Clear();
            Version++;
        }

        Changed?.Invoke();
    }

    /// <summary>The text of a definition's file, or the DSL of definitions without a file.</summary>
    public string Text(TypeDef definition)
    {
        // A library's definition as the project sees it: names qualified, defaults written out.
        if (definition.Owner != null) return DslWriter.Write(definition);
        var file = Sandbox.Check(Project!.FileFor(definition));
        return File.Exists(file) ? File.ReadAllText(file) : DslWriter.Write(definition);
    }

    public bool IsImported(TypeDef definition)
    {
        if (definition.Owner != null) return false;
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

    /// <summary>Writes a sample (see <see cref="FormatProject.SaveSample"/>) and reloads.</summary>
    public string SaveSample(SampleDef sample, string file, SampleDef? original)
    {
        var written = Project!.SaveSample(sample, file, original);
        Reload();
        return written;
    }

    public void DeleteSample(SampleDef sample)
    {
        Project!.DeleteSample(sample);
        Reload();
    }

    /// <summary>
    /// Checks a sample: its value must encode to its hex (when both are given), and its bytes must round trip.
    /// Returns "ok" or what went wrong.
    /// </summary>
    public string CheckSample(SampleDef sample)
    {
        try
        {
            var codec = new Codec(Project!);
            var type = Codec.ParseType(sample.Type);
            byte[] bytes;
            if (sample.Value != null)
            {
                var encoded = codec.Encode(type, sample.Value);
                if (!encoded.Success) return "encode failed: " + encoded.Diagnostics.First(d => d.Severity == Severity.Error).Message;
                if (sample.Hex != null && !Hex.Parse(sample.Hex).AsSpan().SequenceEqual(encoded.Bytes)) return $"differs: {Hex.Format(encoded.Bytes)}";
                bytes = encoded.Bytes;
            }
            else if (sample.Hex != null)
            {
                bytes = Hex.Parse(sample.Hex);
            }
            else
            {
                return "no value or hex";
            }

            var roundTrip = Simulator.RoundTripBytes(codec, type, bytes);
            return roundTrip.Success ? "ok" : "round trip failed: " + roundTrip.Diagnostics.First(d => d.Severity == Severity.Error).Message;
        }
        catch (Exception e) when (e is System.FormatException or FormatDefinitionException or Expressions.ExprException)
        {
            return e.Message;
        }
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
        var kindFolder = FormatProject.KindFolder(definition.Kind);
        return System.IO.Path.Combine(overrideFolder, kindFolder, definition.Name + ".yaml");
    }

    /// <summary>Definitions that use the given type: the project's own and those of its libraries.</summary>
    public IEnumerable<(TypeDef User, string Where)> References(TypeDef target)
    {
        if (Project == null) yield break;
        bool Uses(TypeRef type) => UsesType(type, name => Project.Find(name) == target);
        var definitions = Project.Types.Values.Concat(Project.Modules.Values.SelectMany(m => m.Types.Values));
        foreach (var definition in definitions.OrderBy(d => d.QualifiedName, StringComparer.Ordinal))
        {
            switch (definition)
            {
                case StructDef structDef:
                    foreach (var field in structDef.Fields.Where(f => Uses(f.Type))) yield return (definition, field.Name);
                    break;
                case VariantDef variant:
                    if (variant.Tag != null && Uses(variant.Tag)) yield return (definition, "tag");
                    foreach (var variantCase in variant.Cases.Where(c => c.Type != null && Uses(c.Type)))
                        yield return (definition, variantCase.Name);
                    break;
                case AliasDef alias when Uses(alias.Target):
                    yield return (definition, "type");
                    break;
                case BitfieldDef bitfield:
                    foreach (var member in bitfield.Members.Where(m => m.Type != null && Project.Find(m.Type) == target)) yield return (definition, member.Name);
                    break;
            }
        }
    }

    private static bool UsesType(TypeRef type, Func<string, bool> isTarget) => type switch
    {
        NamedRef named => isTarget(named.Name),
        ArrayRef array => UsesType(array.Element, isTarget),
        MapRef map => UsesType(map.Key, isTarget) || UsesType(map.Value, isTarget),
        OptionalRef optional => UsesType(optional.Inner, isTarget),
        SizedRef sized => UsesType(sized.Inner, isTarget),
        _ => false,
    };

    /// <summary>The output of a codegen target, generated once per project version and target settings.</summary>
    public GeneratedOutput Generated(CodegenTarget target)
    {
        var key = target.Name + "\n" + target.Generator + "\n" + target.Options.ToJsonString();
        lock (_lock)
        {
            if (!_generated.TryGetValue(key, out var output)) _generated[key] = output = CodeGeneration.Generate(Project!, target);
            return output;
        }
    }

    /// <summary>
    /// Targets to show generated code with: the project's codegen targets, then the defaults of each generator that no
    /// target uses.
    /// </summary>
    public IReadOnlyList<CodegenTarget> PreviewTargets
    {
        get
        {
            var targets = Project?.Settings.Codegen ?? [];
            return targets.Concat(CodeGeneration.Generators.Where(g => targets.All(t => t.Generator != g.Name)).Select(CodeGeneration.DefaultTarget)).ToList();
        }
    }

    /// <summary>Writes the protocols to the project file (see <see cref="FormatProject.SaveProtocols"/>) and reloads.</summary>
    public void SaveProtocols(IReadOnlyList<ProtocolDef> protocols)
    {
        Project!.SaveProtocols(protocols);
        Reload();
    }

    /// <summary>Writes the codegen targets to the project file (see <see cref="FormatProject.SaveCodegen"/>) and reloads.</summary>
    public void SaveCodegen(IReadOnlyList<CodegenTarget> targets)
    {
        Project!.SaveCodegen(targets);
        Reload();
    }
}
