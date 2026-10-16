using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Simulation;

namespace FormatStudio.Natives;

/// <summary>
/// A native type declared in a project or library file (<c>native: rgb</c>): its name, parameters and generated code
/// come from the file, its behaviour (decoding, encoding, values) from an <see cref="INativeType"/> class in a .NET
/// plugin. Without the plugin (none given, not allowed, failed to load) everything but decoding and encoding still
/// works: references are checked, code is generated.
/// </summary>
public sealed partial class DeclaredNative : INativeType
{
    private static readonly HashSet<string> Keys = ["native", "doc", "plugin", "class", "parameters", "nullable", "minSize", "codegen"];
    private static readonly HashSet<string> CodegenKeys = ["type", "read", "write", "files", "valueType"];

    private readonly IReadOnlyDictionary<string, NativeCodegen> _codegen;
    private readonly bool? _nullable;
    private readonly int? _minSize;

    private DeclaredNative(string name, IReadOnlyDictionary<string, NativeCodegen> codegen, bool? nullable, int? minSize)
    {
        Name = name;
        _codegen = codegen;
        _nullable = nullable;
        _minSize = minSize;
    }

    public string Name { get; }
    public string Description { get; private init; } = "";
    public IReadOnlyList<NativeParameter> Parameters { get; private init; } = [];

    /// <summary>The declaration file, relative to the project or package.</summary>
    public required SourceLocation Source { get; init; }
    /// <summary>The plugin assembly, relative to the project or package; null when the declaration names none.</summary>
    public string? Plugin { get; private init; }
    /// <summary>The <see cref="INativeType"/> class in the plugin.</summary>
    public string? ClassName { get; private init; }
    /// <summary>The class's instance; null when there is none (see <see cref="Unavailable"/>).</summary>
    public INativeType? Implementation { get; private set; }
    /// <summary>Why there is no <see cref="Implementation"/>; null when there is one.</summary>
    public string? Unavailable { get; private set; }
    /// <summary>The files besides the declaration that it uses (plugin, dependencies, runtime files), to be packed with it.</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    [GeneratedRegex(@"^native\s*:", RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    /// <summary>Whether a source file declares a native type (its top-level mapping has <c>native</c>) rather than a definition.</summary>
    public static bool IsDeclaration(string text) => DeclarationRegex().IsMatch(text);

    /// <summary>
    /// Reads a declaration and loads its plugin. Problems are added to <paramref name="diagnostics"/>: errors for the
    /// declaration, a warning when the policy doesn't allow the plugin. Returns null when the declaration is unusable.
    /// </summary>
    /// <param name="file">The declaration's path in <paramref name="files"/>; plugin and file paths are relative to its folder.</param>
    /// <param name="generators">The generators the codegen of the declaration is checked against; null for no check.</param>
    public static DeclaredNative? Read(string text, string file, NativeFiles files, PluginPolicy policy, DiagnosticBag diagnostics, string? displayFile = null,
        CodeGenerators? generators = null)
    {
        var where = new SourceLocation(displayFile ?? file);
        try
        {
            if (YamlValues.ParseYaml(text) is not JsonObject map) throw new FormatDefinitionException("A native type declaration must be a mapping");
            foreach (var key in map.Select(p => p.Key).Where(k => !Keys.Contains(k)))
                diagnostics.Warning($"Unknown key '{key}' in a native type declaration", where);
            var name = map["native"]?.ToString() ?? "";
            if (!TypeExpressions.IsIdentifier(name)) throw new FormatDefinitionException($"'{name}' is not a valid native type name ([A-Za-z_][A-Za-z0-9_]*)");

            var used = new List<string>();
            var codegen = new Dictionary<string, NativeCodegen>(StringComparer.Ordinal);
            if (map["codegen"] is JsonObject codegenMap)
            {
                foreach (var (generator, node) in codegenMap)
                    codegen[generator] = ReadCodegen(name, generator, node, file, files, used, generators, diagnostics, where);
            }
            else if (map["codegen"] != null) throw new FormatDefinitionException("codegen must map generator names to their code");

            var native = new DeclaredNative(name, codegen, Bool(map, "nullable"), Integer(map, "minSize"))
            {
                Source = where,
                Description = map["doc"]?.ToString() ?? "",
                Parameters = map["parameters"] is { } parameters ? ReadParameters(parameters) : [],
                Plugin = map["plugin"] is { } plugin ? NativeFiles.Join(file, plugin.ToString()) : null,
                ClassName = map["class"]?.ToString(),
            };
            if (native.Plugin != null && native.ClassName == null) throw new FormatDefinitionException($"{name}: a plugin needs the 'class' that implements {nameof(INativeType)}");
            if (native.Plugin == null && native.ClassName != null) throw new FormatDefinitionException($"{name}: 'class' needs the 'plugin' assembly it is in");

            if (native.Plugin == null) native.Unavailable = "no plugin is declared";
            else
            {
                used.AddRange(NativePlugins.Files(files, native.Plugin));
                native.Load(files, policy, diagnostics);
            }

            native.Files = used.Distinct().ToList();
            return native;
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
            Implementation = NativePlugins.Create<INativeType>(files, Plugin!, ClassName!, policy, Name);
        }
        catch (PluginRefusedException e)
        {
            Unavailable = e.Message;
            diagnostics.Warning($"{Name} can't decode or encode: {e.Message}", Source);
        }
        catch (Exception e) when (e is FormatDefinitionException or MissingMethodException or System.Reflection.TargetInvocationException
                                      or FileLoadException or FileNotFoundException or TypeLoadException)
        {
            Unavailable = e.InnerException?.Message ?? e.Message;
            diagnostics.Error(e is FormatDefinitionException ? e.Message : $"{Name}: {ClassName} couldn't be loaded: {Unavailable}", Source);
        }
    }

    private static bool? Bool(JsonObject map, string key) => map[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        var other => throw new FormatDefinitionException($"{key} must be true or false, not '{other}'"),
    };

    private static int? Integer(JsonObject map, string key) => map[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<long>(out var number) && number is >= 0 and <= int.MaxValue => (int)number,
        var other => throw new FormatDefinitionException($"{key} must be a non-negative integer, not '{other}'"),
    };

    private static List<NativeParameter> ReadParameters(JsonNode node)
    {
        if (node is not JsonArray list) throw new FormatDefinitionException("parameters must be a list");
        var result = new List<NativeParameter>();
        foreach (var item in list)
        {
            if (item is not JsonObject parameter || parameter["name"]?.ToString() is not { } name || !TypeExpressions.IsIdentifier(name.Replace('-', '_')))
                throw new FormatDefinitionException("A parameter needs a 'name'");
            if (result.Any(p => p.Name == name)) throw new FormatDefinitionException($"Two parameters are named '{name}'");
            var choices = parameter["choices"] is JsonArray values ? values.Select(v => v?.ToString() ?? "").ToList() : null;
            var kind = parameter["kind"]?.ToString() switch
            {
                null => choices != null ? NativeParameterKind.Choice : NativeParameterKind.Text,
                "choice" => NativeParameterKind.Choice,
                "text" => NativeParameterKind.Text,
                "integer" => NativeParameterKind.Integer,
                "type" => NativeParameterKind.Type,
                var other => throw new FormatDefinitionException($"Unknown kind '{other}' of parameter {name} (choice, text, integer, type)"),
            };
            if (kind == NativeParameterKind.Choice && choices is not { Count: > 0 })
                throw new FormatDefinitionException($"Parameter {name} needs its 'choices'");
            var defaultValue = parameter["default"]?.ToString();
            if (defaultValue != null && choices != null && kind == NativeParameterKind.Choice && !choices.Contains(defaultValue))
                throw new FormatDefinitionException($"The default '{defaultValue}' of parameter {name} isn't one of its choices");
            result.Add(new NativeParameter(name, kind, parameter["doc"]?.ToString() ?? "", choices, defaultValue));
        }

        return result;
    }

    /// <summary>Reads the code of a native type for one generator; also used by <see cref="NativeCodegenExtension"/>.</summary>
    internal static NativeCodegen ReadCodegen(string name, string generator, JsonNode? node, string file, NativeFiles files, List<string> used,
        CodeGenerators? generators, DiagnosticBag diagnostics, SourceLocation where)
    {
        if (node is not JsonObject map) throw new FormatDefinitionException($"codegen.{generator} must be a mapping");
        if (generators != null && generators.Find(generator) == null)
            diagnostics.Warning($"Unknown generator '{generator}' in the codegen of {name} ({string.Join(", ", generators.All.Select(g => g.Name))})", where);
        foreach (var key in map.Select(p => p.Key).Where(k => !CodegenKeys.Contains(k)))
            diagnostics.Warning($"Unknown key '{key}' in codegen.{generator}", where);
        string Template(string key) => map[key]?.ToString() is { Length: > 0 } text
            ? text
            : throw new FormatDefinitionException($"codegen.{generator} of {name} needs '{key}'");

        var runtimeFiles = new List<NativeFile>();
        var patterns = map["files"] switch
        {
            null => [],
            JsonArray list => list.Select(p => p?.ToString() ?? "").ToList(),
            var single => [single.ToString()],
        };
        foreach (var pattern in patterns)
        {
            var matches = files.Glob(NativeFiles.Join(file, pattern));
            if (matches.Count == 0) diagnostics.Warning($"codegen.{generator} of {name}: no files match {pattern}", where);
            foreach (var match in matches)
            {
                var fileName = Path.GetFileName(match);
                if (runtimeFiles.Any(f => f.Name == fileName))
                    throw new FormatDefinitionException($"codegen.{generator} of {name} has two files named {fileName}");
                runtimeFiles.Add(new NativeFile(fileName, Encoding.UTF8.GetString(files.Read(match)!)));
                used.Add(match);
            }
        }

        return new NativeCodegen(Template("type"), Template("read"), Template("write"), runtimeFiles,
            map["valueType"] is JsonValue valueType && valueType.TryGetValue<bool>(out var isValueType) && isValueType);
    }

    private INativeType Implemented => Implementation
        ?? throw new FormatDefinitionException($"{Name} can't decode or encode: {Unavailable}", Source);

    /// <summary>Runs plugin code; its exceptions other than the expected ones become definition errors.</summary>
    private T Call<T>(Func<INativeType, T> call)
    {
        var implementation = Implemented;
        try
        {
            return call(implementation);
        }
        catch (Exception e) when (e is not (DecodeException or EncodeException or FormatDefinitionException or ExprException or OperationCanceledException))
        {
            throw new FormatDefinitionException($"{Name} failed: {e.GetType().Name}: {e.Message}", Source);
        }
    }

    public bool Nullable => _nullable ?? Implementation?.Nullable ?? false;

    public int MinSize(NativeArguments args) => _minSize ?? (Implementation == null ? 0 : Call(type => type.MinSize(args)));

    public JsonNode? Read(INativeReader reader, NativeArguments args) => Call(type => type.Read(reader, args));

    public void Write(INativeWriter writer, NativeArguments args, JsonNode? value) => Call(type =>
    {
        type.Write(writer, args, value);
        return 0;
    });

    // Without the plugin, new values start out empty instead of failing, so that the type can still be edited.
    public JsonNode? DefaultValue(NativeArguments args) => Implementation == null ? null : Call(type => type.DefaultValue(args));

    public JsonNode? RandomValue(NativeArguments args, Random random, int depth) => Call(type => type.RandomValue(args, random, depth));

    public object? ExpressionValue(JsonNode? value, NativeArguments args) =>
        Implementation == null ? value?.ToJsonString() : Call(type => type.ExpressionValue(value, args));

    public NativeCodegen? Codegen(string generator) => _codegen.TryGetValue(generator, out var codegen)
        ? codegen
        : Implementation == null ? null : Call(type => type.Codegen(generator));

    public override string ToString() => Plugin == null ? Name : $"{Name} ({Plugin})";
}
