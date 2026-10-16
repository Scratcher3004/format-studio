using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;

namespace FormatStudio.Codegen;

/// <summary>
/// A code generator declared in a project or library file (<c>generator: rust</c>) and implemented by an
/// <see cref="ICodeGenerator"/> class in a .NET plugin, usually built on <see cref="CodeEmitter"/>. Without the plugin
/// (not allowed, failed to load) the generator still exists, so that targets naming it stay valid, but generating
/// reports why it can't.
/// </summary>
public sealed partial class DeclaredGenerator : ICodeGenerator
{
    private static readonly HashSet<string> Keys = ["generator", "doc", "plugin", "class"];

    private string _title;
    private string _language = "text";
    private string _description;
    private string _defaultOutput;
    private string? _marker;
    private IReadOnlyList<CodegenOption> _options = [];

    private DeclaredGenerator(string name, string doc)
    {
        Name = name;
        _title = name;
        _description = doc;
        _defaultOutput = $"generated/{name}";
    }

    public string Name { get; }
    public string Title => _title;
    public string Language => _language;
    public string Description => _description;
    public string DefaultOutput => _defaultOutput;
    public IReadOnlyList<CodegenOption> Options => _options;

    /// <summary>The plugin's marker; throws when there is no plugin, so that no file is overwritten or deleted.</summary>
    public string Marker => _marker ?? throw new FormatDefinitionException(CantGenerate, Source);

    /// <summary>The declaration file, relative to the project or package.</summary>
    public required SourceLocation Source { get; init; }
    /// <summary>The plugin assembly, relative to the project or package.</summary>
    public required string Plugin { get; init; }
    /// <summary>The <see cref="ICodeGenerator"/> class in the plugin.</summary>
    public required string ClassName { get; init; }
    /// <summary>The class's instance; null when there is none (see <see cref="Unavailable"/>).</summary>
    public ICodeGenerator? Implementation { get; private set; }
    /// <summary>Why there is no <see cref="Implementation"/>; null when there is one.</summary>
    public string? Unavailable { get; private set; }
    /// <summary>The plugin and its dependencies, to be packed with the declaration.</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    private string CantGenerate => $"{Name} can't generate code: {Unavailable}";

    [GeneratedRegex(@"^generator\s*:", RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex NameRegex();

    /// <summary>Whether a source file declares a code generator (its top-level mapping has <c>generator</c>).</summary>
    public static bool IsDeclaration(string text) => DeclarationRegex().IsMatch(text);

    /// <summary>
    /// Reads a declaration and loads its plugin. Problems are added to <paramref name="diagnostics"/>: errors for the
    /// declaration, a warning when the policy doesn't allow the plugin. Returns null when the declaration is unusable.
    /// </summary>
    /// <param name="file">The declaration's path in <paramref name="files"/>; the plugin path is relative to its folder.</param>
    public static DeclaredGenerator? Read(string text, string file, NativeFiles files, PluginPolicy policy, DiagnosticBag diagnostics, string? displayFile = null)
    {
        var where = new SourceLocation(displayFile ?? file);
        try
        {
            if (YamlValues.ParseYaml(text) is not JsonObject map) throw new FormatDefinitionException("A generator declaration must be a mapping");
            foreach (var key in map.Select(p => p.Key).Where(k => !Keys.Contains(k)))
                diagnostics.Warning($"Unknown key '{key}' in a generator declaration", where);
            var name = map["generator"]?.ToString() ?? "";
            if (!NameRegex().IsMatch(name)) throw new FormatDefinitionException($"'{name}' is not a valid generator name ([A-Za-z_][A-Za-z0-9_-]*)");
            var plugin = map["plugin"]?.ToString() ?? throw new FormatDefinitionException($"Generator {name} needs the 'plugin' assembly that implements it");
            var className = map["class"]?.ToString() ?? throw new FormatDefinitionException($"Generator {name} needs the 'class' that implements {nameof(ICodeGenerator)}");

            var generator = new DeclaredGenerator(name, map["doc"]?.ToString() ?? "")
            {
                Source = where,
                Plugin = NativeFiles.Join(file, plugin),
                ClassName = className,
            };
            generator.Files = NativePlugins.Files(files, generator.Plugin);
            generator.Load(files, policy, diagnostics);
            return generator;
        }
        catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException or InvalidOperationException or System.FormatException)
        {
            diagnostics.Error(e.Message, where);
            return null;
        }
    }

    private void Load(NativeFiles files, PluginPolicy policy, DiagnosticBag diagnostics)
    {
        try
        {
            var implementation = NativePlugins.Create<ICodeGenerator>(files, Plugin, ClassName, policy, Name);
            // Read once: the values are shown and checked, and a broken plugin is reported here rather than in the UI.
            var marker = implementation.Marker;
            if (string.IsNullOrWhiteSpace(marker))
                throw new FormatDefinitionException($"{Name}: {ClassName} has no Marker; without one, generated files can't be told from others");
            var defaultOutput = implementation.DefaultOutput;
            CodeGeneration.CheckOutput(defaultOutput);
            _title = implementation.Title is { Length: > 0 } title ? title : Name;
            _language = implementation.Language is { Length: > 0 } language ? language : "text";
            if (_description.Length == 0) _description = implementation.Description ?? "";
            _options = implementation.Options ?? [];
            _defaultOutput = defaultOutput;
            _marker = marker;
            Implementation = implementation;
        }
        catch (PluginRefusedException e)
        {
            Unavailable = e.Message;
            diagnostics.Warning(CantGenerate, Source);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Unavailable = e is FormatDefinitionException ? e.Message : e.InnerException?.Message ?? e.Message;
            diagnostics.Error(e is FormatDefinitionException ? e.Message : $"{Name}: {ClassName} couldn't be loaded: {Unavailable}", Source);
        }
    }

    public GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped)
    {
        if (Implementation == null)
        {
            var output = new GeneratedOutput();
            output.Diagnostics.Error(CantGenerate, Source);
            return output;
        }

        try
        {
            return Implementation.Generate(project, options, skipped);
        }
        catch (Exception e) when (e is not (FormatDefinitionException or OperationCanceledException))
        {
            var output = new GeneratedOutput();
            output.Diagnostics.Error($"{Name} failed: {e.GetType().Name}: {e.Message}", Source);
            return output;
        }
    }

    public override string ToString() => $"{Name} ({Plugin})";
}
