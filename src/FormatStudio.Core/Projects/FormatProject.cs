using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Natives;
using Microsoft.Extensions.FileSystemGlobbing;

namespace FormatStudio.Projects;

/// <summary>Project-wide defaults for everything a type expression leaves open.</summary>
public sealed class FormatDefaults
{
    public Endian Endian { get; set; } = Endian.Little;
    /// <summary>The prefix of strings, bytes, arrays and maps with a default length.</summary>
    public PrimitiveRef Length { get; set; } = new(PrimitiveKind.VarUInt32);
    public StringEncoding String { get; set; } = StringEncoding.Utf8;
    /// <summary>The check of padded strings that don't name one.</summary>
    public PaddingCheck Check { get; set; } = PaddingCheck.Warn;

    /// <summary>
    /// The arguments of native types that a reference leaves out, by type name: <c>nbt: java</c> gives <c>nbt</c> the
    /// flavor <c>java</c>.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> Natives { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Libraries only: the settings (<see cref="Names"/> or native type names) that come from the project using the
    /// library instead of from these defaults.
    /// </summary>
    public HashSet<string> Inherit { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Libraries only: every setting comes from the project using the library (<c>inherit: true</c>).</summary>
    public bool InheritAll { get; set; }

    /// <summary>The names of the settings other than native types, as in the project file.</summary>
    public static IReadOnlyList<string> Names { get; } = ["endian", "length", "string", "check"];

    public bool Inherits(string setting) => InheritAll || Inherit.Contains(setting);
}

public enum ProjectKind
{
    /// <summary>A format of its own, with packets and code generation.</summary>
    Project,
    /// <summary>Definitions for other projects to use; packed into a <c>.fslib</c> package.</summary>
    Library,
}

/// <summary>A library the project uses: its definitions are referenced as <c>Alias.Name</c>, or by name when unambiguous.</summary>
public sealed class LibraryDependency
{
    /// <summary>The library's name.</summary>
    public required string Library { get; set; }
    /// <summary>The exact version; null for the highest version found.</summary>
    public string? Version { get; set; }
    /// <summary>The name the project uses for the library; null for its name.</summary>
    public string? Alias { get; set; }
    /// <summary>Code generation leaves the library's definitions out: their code comes from elsewhere.</summary>
    public bool SkipCodegen { get; set; }
    /// <summary>The C# namespace of the library's types; null for a namespace inside the target's.</summary>
    public string? Namespace { get; set; }
    /// <summary>The Java package of the library's types; null for a package inside the target's.</summary>
    public string? Package { get; set; }

    public string AliasOrName => Alias ?? Library;
}

public sealed class CodegenTarget
{
    public required string Name { get; set; }
    public required string Generator { get; set; }
    /// <summary>Output folder, relative to the project directory.</summary>
    public required string Output { get; set; }
    public JsonObject Options { get; set; } = [];
}

/// <summary>
/// A set of packets with ids of their own, e.g. one state and direction of a network protocol. Its packets are those
/// in all of its <see cref="Groups"/>; ids only have to be unique within a protocol, and code generation writes a
/// registry per protocol.
/// </summary>
public sealed class ProtocolDef
{
    public required string Name { get; set; }
    public List<string> Groups { get; set; } = [];
    public string? Doc { get; set; }

    public bool Contains(StructDef packet) => packet.IsPacket && Groups.All(packet.Groups.Contains);
}

/// <summary>The contents of a <c>*.fstudio.yaml</c> project file.</summary>
public sealed class ProjectSettings
{
    public const string Extension = ".fstudio.yaml";

    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public string? Version { get; set; }
    public ProjectKind Kind { get; set; } = ProjectKind.Project;
    public FormatDefaults Defaults { get; set; } = new();
    public List<LibraryDependency> Dependencies { get; set; } = [];
    /// <summary>Folders with <c>.fslib</c> packages, relative to the project; the built-in libraries come after them.</summary>
    public List<string> Libraries { get; set; } = ["libraries"];
    /// <summary>Globs of definition files. A type in a later glob replaces one with the same name in an earlier glob.</summary>
    public List<string> Sources { get; set; } = ["**/*.yaml", $"!**/*{Extension}"];
    public List<string> Tests { get; set; } = [];
    /// <summary>Importer settings, keyed by importer name.</summary>
    public JsonObject Import { get; set; } = [];
    /// <summary>Separate packet id spaces; empty when all packets share one.</summary>
    public List<ProtocolDef> Protocols { get; set; } = [];
    public List<CodegenTarget> Codegen { get; set; } = [];

    public static ProjectSettings Parse(string yaml, string file)
    {
        if (YamlValues.ParseYaml(yaml) is not JsonObject root)
            throw new FormatDefinitionException("A project file must be a mapping", new SourceLocation(file));

        var settings = new ProjectSettings
        {
            Name = root["name"]?.GetValue<string>() ?? Path.GetFileName(file).Replace(Extension, ""),
            Description = root["description"]?.ToString(),
            Version = root["version"]?.ToString(),
            Kind = root["kind"]?.ToString() switch
            {
                null or "project" => ProjectKind.Project,
                "library" => ProjectKind.Library,
                var kind => throw new FormatDefinitionException($"Unknown kind '{kind}' (project, library)", new SourceLocation(file)),
            },
        };

        if (root["defaults"] is JsonObject defaults)
        {
            if (defaults["endian"]?.GetValue<string>() is { } endian)
            {
                settings.Defaults.Endian = endian switch
                {
                    "little" or "le" => Endian.Little,
                    "big" or "be" => Endian.Big,
                    _ => throw new FormatDefinitionException($"Unknown endian '{endian}' (little, big)", new SourceLocation(file)),
                };
            }

            if (defaults["length"]?.GetValue<string>() is { } length)
            {
                if (!Primitives.TryParse(length, out var prefix) || !Primitives.IsInteger(prefix.Kind))
                    throw new FormatDefinitionException($"Default length must be an integer type, not '{length}'", new SourceLocation(file));
                settings.Defaults.Length = prefix;
            }

            if (defaults["string"]?.GetValue<string>() is { } encoding)
                settings.Defaults.String = Wrap(() => TypeExpressions.ParseEncoding(encoding), file);
            if (defaults["check"]?.GetValue<string>() is { } check)
                settings.Defaults.Check = Wrap(() => TypeExpressions.ParseCheck(check), file);
            foreach (var (key, value) in defaults)
            {
                // Everything else names a native type (checked by the validator, which knows the project's types).
                if (FormatDefaults.Names.Contains(key) || key == "inherit") continue;
                settings.Defaults.Natives[key] = value switch
                {
                    JsonArray args => args.Select(arg => arg?.ToString() ?? "").ToList(),
                    null => [],
                    _ => [value.ToString()],
                };
            }

            switch (defaults["inherit"])
            {
                case JsonValue all when all.GetValueKind() == System.Text.Json.JsonValueKind.True:
                    settings.Defaults.InheritAll = true;
                    break;
                case JsonArray names:
                    foreach (var name in names.Select(n => n?.ToString() ?? ""))
                    {
                        if (!FormatDefaults.Names.Contains(name) && !TypeExpressions.IsTypeName(name))
                            throw new FormatDefinitionException($"Unknown default '{name}' in inherit ({string.Join(", ", FormatDefaults.Names)} or a native type)", new SourceLocation(file));
                        settings.Defaults.Inherit.Add(name);
                    }

                    break;
            }
        }

        if (root["dependencies"] is JsonArray dependencies)
        {
            foreach (var dependency in dependencies)
            {
                if (dependency is not JsonObject map || map["library"]?.ToString() is not { } library)
                    throw new FormatDefinitionException("A dependency needs a 'library'", new SourceLocation(file));
                var dependencyCodegen = map["codegen"] as JsonObject;
                settings.Dependencies.Add(new LibraryDependency
                {
                    Library = library,
                    Version = map["version"]?.ToString(),
                    Alias = map["as"]?.ToString(),
                    SkipCodegen = dependencyCodegen?["skip"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
                    Namespace = dependencyCodegen?["namespace"]?.ToString(),
                    Package = dependencyCodegen?["package"]?.ToString(),
                });
            }
        }

        if (root["libraries"] is JsonArray libraries) settings.Libraries = libraries.Select(l => l!.GetValue<string>()).ToList();

        if (root["sources"] is JsonArray sources) settings.Sources = sources.Select(s => s!.GetValue<string>()).ToList();
        if (root["tests"] != null && root["samples"] != null)
            throw new FormatDefinitionException("Use 'tests' for the test patterns ('samples' is its old name), not both", new SourceLocation(file));
        if ((root["tests"] ?? root["samples"]) is JsonArray tests) settings.Tests = tests.Select(s => s!.GetValue<string>()).ToList();
        if (root["import"] is JsonObject import) settings.Import = (JsonObject)import.DeepClone();
        if (root["protocols"] is JsonArray protocols)
        {
            foreach (var protocol in protocols)
            {
                if (protocol is not JsonObject map || map["name"]?.ToString() is not { } name)
                    throw new FormatDefinitionException("A protocol needs a 'name'", new SourceLocation(file));
                settings.Protocols.Add(new ProtocolDef
                {
                    Name = name,
                    Groups = map["groups"] switch
                    {
                        JsonArray groups => groups.Select(g => g!.ToString()).ToList(),
                        JsonValue group => [group.ToString()],
                        _ => [],
                    },
                    Doc = map["doc"]?.ToString(),
                });
            }
        }

        if (root["codegen"] is JsonArray codegen)
        {
            foreach (var target in codegen.OfType<JsonObject>())
            {
                settings.Codegen.Add(new CodegenTarget
                {
                    Name = target["name"]?.GetValue<string>() ?? target["generator"]?.GetValue<string>() ?? "codegen",
                    Generator = target["generator"]?.GetValue<string>()
                        ?? throw new FormatDefinitionException("A codegen target needs a 'generator'", new SourceLocation(file)),
                    Output = target["output"]?.GetValue<string>()
                        ?? throw new FormatDefinitionException("A codegen target needs an 'output' folder", new SourceLocation(file)),
                    Options = target["options"] is JsonObject options ? (JsonObject)options.DeepClone() : [],
                });
            }
        }

        return settings;
    }

    private static T Wrap<T>(Func<T> parse, string file)
    {
        try
        {
            return parse();
        }
        catch (System.FormatException e)
        {
            throw new FormatDefinitionException(e.Message, new SourceLocation(file));
        }
    }

    /// <summary>
    /// The top-level keys of the project file that the settings page edits (<see cref="ReplaceGeneral"/>), in the
    /// order <see cref="ToYaml"/> writes them. Imports, protocols and codegen targets have pages of their own.
    /// </summary>
    public static IReadOnlyList<string> GeneralKeys { get; } = ["name", "description", "version", "kind", "defaults", "dependencies", "libraries", "sources", "tests"];

    public string ToYaml()
    {
        var map = GeneralYaml();
        if (Import.Count > 0) map.Add("import", Import);
        map.AddIf("protocols", ProtocolsYaml(Protocols));
        map.AddIf("codegen", CodegenYaml(Codegen));
        return YamlEmitter.Write(map);
    }

    /// <summary>The entries of <see cref="GeneralKeys"/> that differ from their defaults.</summary>
    private YMap GeneralYaml()
    {
        var map = new YMap { { "name", Name } };
        map.AddIf("description", Description);
        map.AddIf("version", Version);
        if (Kind == ProjectKind.Library) map.Add("kind", "library");
        var defaults = new YMap
        {
            { "endian", Defaults.Endian == Endian.Little ? "little" : "big" },
            { "length", Defaults.Length.ToString() },
            { "string", TypeExpressions.EncodingName(Defaults.String) },
        };
        if (Defaults.Check != PaddingCheck.Warn) defaults.Add("check", TypeExpressions.CheckName(Defaults.Check));
        foreach (var (native, args) in Defaults.Natives)
            defaults.Add(native, args.Count == 1 ? args[0] : args.Cast<object?>().ToList());
        if (Defaults.InheritAll) defaults.Add("inherit", true);
        else
        {
            defaults.AddIf("inherit", FormatDefaults.Names.Where(Defaults.Inherit.Contains)
                .Concat(Defaults.Inherit.Where(name => !FormatDefaults.Names.Contains(name)).Order(StringComparer.Ordinal))
                .Cast<object?>().ToList());
        }

        map.Add("defaults", defaults);
        map.AddIf("dependencies", Dependencies.Select(dependency =>
        {
            var dependencyMap = new YMap { { "library", dependency.Library } };
            dependencyMap.AddIf("version", dependency.Version);
            dependencyMap.AddIf("as", dependency.Alias);
            var codegen = new YMap();
            if (dependency.SkipCodegen) codegen.Add("skip", true);
            codegen.AddIf("namespace", dependency.Namespace);
            codegen.AddIf("package", dependency.Package);
            if (codegen.Count > 0) dependencyMap.Add("codegen", codegen);
            return (object?)dependencyMap;
        }).ToList());
        if (Libraries is not ["libraries"]) map.Add("libraries", Libraries.Cast<object?>().ToList());
        map.Add("sources", Sources.Cast<object?>().ToList());
        map.AddIf("tests", Tests.Cast<object?>().ToList());
        return map;
    }

    private static List<object?> CodegenYaml(IEnumerable<CodegenTarget> targets) => targets.Select(target =>
    {
        var targetMap = new YMap { { "name", target.Name }, { "generator", target.Generator }, { "output", target.Output } };
        if (target.Options.Count > 0) targetMap.Add("options", target.Options);
        return (object?)targetMap;
    }).ToList();

    private static List<object?> ProtocolsYaml(IEnumerable<ProtocolDef> protocols) => protocols.Select(protocol =>
    {
        var protocolMap = new YMap { { "name", protocol.Name }, { "groups", protocol.Groups.Cast<object?>().ToList() } };
        protocolMap.AddIf("doc", protocol.Doc);
        return (object?)protocolMap;
    }).ToList();

    /// <summary>
    /// Replaces the top-level <c>codegen</c> section of a project file's text with <paramref name="targets"/> (removes it
    /// when there are none, appends it when there was none). The rest of the file, comments included, stays as it is.
    /// </summary>
    public static string ReplaceCodegen(string text, IReadOnlyList<CodegenTarget> targets) =>
        ReplaceSection(text, "codegen", CodegenYaml(targets));

    /// <summary>Replaces the top-level <c>protocols</c> section of a project file's text, like <see cref="ReplaceCodegen"/>.</summary>
    public static string ReplaceProtocols(string text, IReadOnlyList<ProtocolDef> protocols) =>
        ReplaceSection(text, "protocols", ProtocolsYaml(protocols));

    /// <summary>
    /// Replaces the <see cref="GeneralKeys"/> of a project file's text with those of <paramref name="settings"/>, like
    /// <see cref="ReplaceCodegen"/>: keys at their defaults are removed, new keys go after the key before them. The other
    /// sections and the comments around the sections stay; comments inside a replaced section don't.
    /// </summary>
    public static string ReplaceGeneral(string text, ProjectSettings settings)
    {
        var values = settings.GeneralYaml().ToDictionary(e => e.Key, e => e.Value);
        for (var i = 0; i < GeneralKeys.Count; i++)
            text = ReplaceSection(text, GeneralKeys[i], values.GetValueOrDefault(GeneralKeys[i]), GeneralKeys.Take(i).ToList());
        // The old name of 'tests', written as 'tests' from now on.
        return ReplaceSection(text, "samples", null, null);
    }

    private static string ReplaceSection(string text, string key, List<object?> items) =>
        ReplaceSection(text, key, items.Count == 0 ? null : items, null);

    /// <summary>
    /// Replaces the top-level <paramref name="key"/> with <paramref name="value"/>, or removes it when the value is null. A
    /// missing key goes after the last of the <paramref name="before"/> keys in the text (at the start when there is none
    /// of them), or at the end without <paramref name="before"/>.
    /// </summary>
    private static string ReplaceSection(string text, string key, object? value, IReadOnlyList<string>? before)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var section = value == null
            ? []
            : YamlEmitter.Write(new YMap { { key, value } }).TrimEnd('\n').Split('\n');
        var start = FindKey(lines, key);
        if (start < 0)
        {
            if (section.Length == 0) return text;
            if (before != null)
            {
                var after = before.Select(k => FindKey(lines, k)).Where(i => i >= 0).Select(i => SectionEnd(lines, i)).DefaultIfEmpty(0).Max();
                lines.InsertRange(after, section);
                return string.Join(newline, lines);
            }

            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(section);
            lines.Add("");
            return string.Join(newline, lines);
        }

        var end = SectionEnd(lines, start);
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, section);
        return string.Join(newline, lines);
    }

    private static int FindKey(List<string> lines, string key) =>
        lines.FindIndex(line => System.Text.RegularExpressions.Regex.IsMatch(line, $@"^{key}\s*:"));

    /// <summary>The line after the section starting at <paramref name="start"/>, trailing empty lines excluded.</summary>
    private static int SectionEnd(List<string> lines, int start)
    {
        // The section goes on while lines are indented, empty or list items at the margin ("- name: ...").
        var end = start + 1;
        while (end < lines.Count && (lines[end].Length == 0 || lines[end][0] is ' ' or '\t' || lines[end].StartsWith('-') && !lines[end].StartsWith("---")))
            end++;
        while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
        return end;
    }
}

/// <summary>A loaded project: settings plus all definitions, keyed by name.</summary>
public sealed class FormatProject
{
    private readonly Dictionary<string, TypeDef> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormatModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormatModule> _dependencies = new(StringComparer.Ordinal);

    public ProjectSettings Settings { get; }
    /// <summary>The project file, or null for an in-memory project.</summary>
    public string? FilePath { get; }
    public string Directory { get; }
    /// <summary>Where the project may read and write files; definition files outside it are not loaded.</summary>
    public FileSandbox Sandbox { get; init; } = FileSandbox.Unrestricted;
    public DiagnosticBag Diagnostics { get; } = [];
    public List<TestCaseDef> Tests { get; } = [];
    /// <summary>The native types the project's definitions can use: built-in, its own and its libraries'.</summary>
    public NativeTypes Natives { get; set; } = NativeTypes.BuiltIn;
    /// <summary>The native types the project declares itself (<c>native:</c> files among its sources).</summary>
    public List<DeclaredNative> DeclaredNatives { get; } = [];
    /// <summary>The code for native types the project adds itself (<c>native-codegen:</c> files among its sources).</summary>
    public List<NativeCodegenExtension> CodegenExtensions { get; } = [];
    /// <summary>The code generators the project's targets can use: built-in, its own and its libraries'.</summary>
    public CodeGenerators Generators { get; set; } = CodeGenerators.BuiltIn;
    /// <summary>The code generators the project declares itself (<c>generator:</c> files among its sources).</summary>
    public List<DeclaredGenerator> DeclaredGenerators { get; } = [];
    /// <summary>Which plugins (native types, code generators) may be loaded, the project's and its libraries'.</summary>
    public PluginPolicy Plugins { get; init; } = PluginPolicy.All;

    public FormatProject(ProjectSettings settings, string directory, string? filePath = null)
    {
        Settings = settings;
        Directory = directory;
        FilePath = filePath;
    }

    public FormatDefaults Defaults => Settings.Defaults;
    /// <summary>The project's own definitions (not those of its libraries).</summary>
    public IReadOnlyDictionary<string, TypeDef> Types => _types;
    public IEnumerable<StructDef> Packets => _types.Values.OfType<StructDef>().Where(s => s.IsPacket);

    /// <summary>Every library the project loads, dependencies of dependencies included, by name.</summary>
    public IReadOnlyDictionary<string, FormatModule> Modules => _modules;
    /// <summary>The libraries the project depends on directly, by the alias it uses for them.</summary>
    public IReadOnlyDictionary<string, FormatModule> Dependencies => _dependencies;

    /// <summary>
    /// Adds a library. With an <paramref name="alias"/> it is a direct dependency, whose definitions the project may
    /// reference by name alone; without, it is only loaded for other libraries (referenced as <c>Name.Type</c>).
    /// </summary>
    public void AddModule(FormatModule module, string? alias = null)
    {
        if (_modules.TryGetValue(module.Name, out var existing) && existing != module)
            throw new FormatDefinitionException($"Two different libraries named '{module.Name}' ({existing}, {module})");
        _modules[module.Name] = module;
        if (alias == null) return;
        if (_dependencies.TryGetValue(alias, out var other) && other != module)
            throw new FormatDefinitionException($"The alias '{alias}' is used for both {other} and {module}");
        _dependencies[alias] = module;
    }

    public TypeDef? Find(string name) => Resolve(name).Definition;

    /// <summary>
    /// Resolves a type name: <c>Alias.Type</c> or <c>Library.Type</c> in that library; a plain name in the project's own
    /// definitions first, then in the direct dependencies, where it must be unique.
    /// </summary>
    public TypeLookup Resolve(string name)
    {
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            var qualifier = name[..dot];
            var module = _dependencies.GetValueOrDefault(qualifier) ?? _modules.GetValueOrDefault(qualifier);
            if (module == null) return TypeLookup.Failed($"Unknown library '{qualifier}' in '{name}'");
            return module.Find(name[(dot + 1)..]) is { } qualified
                ? TypeLookup.Found(qualified)
                : TypeLookup.Failed($"Library {module} has no type '{name[(dot + 1)..]}'");
        }

        if (_types.TryGetValue(name, out var own)) return TypeLookup.Found(own);
        var candidates = _dependencies
            .Where(pair => pair.Value.Find(name) != null)
            .GroupBy(pair => pair.Value)
            .Select(group => (Alias: group.First().Key, Definition: group.Key.Find(name)!))
            .ToList();
        return candidates.Count switch
        {
            0 => TypeLookup.Failed($"Unknown type '{name}'"),
            1 => TypeLookup.Found(candidates[0].Definition),
            _ => TypeLookup.Failed($"'{name}' is ambiguous: {string.Join(", ", candidates.Select(c => $"{c.Alias}.{name}"))}"),
        };
    }

    public ProtocolDef? FindProtocol(string name) => Settings.Protocols.FirstOrDefault(p => p.Name == name);

    public IEnumerable<StructDef> PacketsOf(ProtocolDef protocol) => Packets.Where(protocol.Contains);

    /// <summary>The protocols a packet belongs to.</summary>
    public IEnumerable<ProtocolDef> ProtocolsOf(StructDef packet) => Settings.Protocols.Where(p => p.Contains(packet));

    public void Add(TypeDef definition)
    {
        Normalize(definition);
        _types[definition.Name] = definition;
    }

    /// <summary>
    /// A type expression with the bare names of native types as <see cref="NativeRef"/>: without arguments, the parser
    /// can't tell <c>rgb</c> from the name of a definition. Native types take precedence. The type arguments of native
    /// types are parsed into <see cref="NativeRef.Types"/> (those that don't parse are left out, for binding to report).
    /// </summary>
    public TypeRef Normalize(TypeRef type) => type switch
    {
        NamedRef { As: null, Tag: null } named when Natives.Find(named.Name) != null => new NativeRef(named.Name),
        ArrayRef array => array with { Element = Normalize(array.Element) },
        MapRef map => map with { Key = Normalize(map.Key), Value = Normalize(map.Value) },
        OptionalRef optional => new OptionalRef(Normalize(optional.Inner)),
        SizedRef sized => sized with { Inner = Normalize(sized.Inner) },
        NativeRef native => WithTypes(native),
        _ => type,
    };

    private NativeRef WithTypes(NativeRef native)
    {
        if (Natives.Find(native.Name) is not { } nativeType || !nativeType.Parameters.Any(p => p.Kind == NativeParameterKind.Type)) return native;
        var types = new TypeRef?[native.Args.Count];
        for (var i = 0; i < types.Length && i < nativeType.Parameters.Count; i++)
        {
            var parameter = nativeType.Parameters[i];
            if (parameter.Kind != NativeParameterKind.Type || native.Args[i].Trim() is "" or "default") continue;
            try
            {
                types[i] = Normalize(NativeTypes.ParseType(native.Name, parameter, native.Args[i].Trim()));
            }
            catch (FormatDefinitionException)
            {
            }
        }

        return native with { Types = types };
    }

    private void Normalize(TypeDef definition)
    {
        switch (definition)
        {
            case StructDef structDef:
                foreach (var field in structDef.Fields) field.Type = Normalize(field.Type);
                break;
            case VariantDef variant:
                if (variant.Tag != null) variant.Tag = Normalize(variant.Tag);
                foreach (var variantCase in variant.Cases.Where(c => c.Type != null)) variantCase.Type = Normalize(variantCase.Type!);
                break;
            case AliasDef alias:
                alias.Target = Normalize(alias.Target);
                break;
        }
    }

    public bool Remove(string name) => _types.Remove(name);

    /// <summary>
    /// A copy of the project with <paramref name="definition"/> added, or replacing the one with its name, e.g. to decode
    /// with a definition that is still being edited. The definitions themselves are shared, not copied.
    /// </summary>
    public FormatProject With(TypeDef definition)
    {
        var copy = new FormatProject(Settings, Directory, FilePath) { Sandbox = Sandbox, Natives = Natives, Generators = Generators, Plugins = Plugins };
        copy.DeclaredNatives.AddRange(DeclaredNatives);
        copy.DeclaredGenerators.AddRange(DeclaredGenerators);
        copy.CodegenExtensions.AddRange(CodegenExtensions);
        foreach (var type in _types.Values) copy.Add(type);
        foreach (var module in _modules.Values) copy.AddModule(module);
        foreach (var (alias, module) in _dependencies) copy.AddModule(module, alias);
        copy.Add(definition);
        return copy;
    }

    /// <summary>Adds native types; one named like a type the project already has is an error.</summary>
    public void AddNatives(IEnumerable<INativeType> types, SourceLocation? where = null) =>
        Natives = Natives.With(types, (existing, added) =>
            Diagnostics.Error($"Two native types are named '{added.Name}': {NativeOrigin(existing)} and {NativeOrigin(added)}",
                (added as DeclaredNative)?.Source ?? where));

    private static string NativeOrigin(INativeType type) => type switch
    {
        DeclaredNative declared => declared.Source.File,
        _ when NativeTypes.IsBuiltIn(type.Name) => "built-in",
        _ => type.GetType().FullName ?? type.Name,
    };

    /// <summary>Adds code of native types for other generators; problems (unknown type, two sources of code) are errors.</summary>
    public void AddCodegen(IEnumerable<NativeCodegenExtension> extensions) =>
        Natives = Natives.WithCodegen(extensions, (extension, message) => Diagnostics.Error(message, extension.Source));

    /// <summary>Adds code generators; one named like a generator the project already has is an error.</summary>
    public void AddGenerators(IEnumerable<ICodeGenerator> generators, SourceLocation? where = null) =>
        Generators = Generators.With(generators, (existing, added) =>
            Diagnostics.Error($"Two generators are named '{added.Name}': {GeneratorOrigin(existing)} and {GeneratorOrigin(added)}",
                (added as DeclaredGenerator)?.Source ?? where));

    private static string GeneratorOrigin(ICodeGenerator generator) => generator switch
    {
        DeclaredGenerator declared => declared.Source.File,
        _ when CodeGenerators.IsBuiltIn(generator.Name) => "built-in",
        _ => generator.GetType().FullName ?? generator.Name,
    };

    /// <summary>The native type of a reference, with its arguments completed from the project's defaults.</summary>
    public BoundNative Bind(NativeRef native) => Natives.Bind(native.Types.Count == native.Args.Count ? native : WithTypes(native), Defaults, this);

    /// <summary>Follows aliases until a non-alias type expression.</summary>
    public TypeRef Unalias(TypeRef type)
    {
        for (var depth = 0; depth < 64; depth++)
        {
            if (type is not NamedRef { As: null, Tag: null } named || Find(named.Name) is not AliasDef alias) return type;
            type = alias.Target;
        }

        throw new FormatDefinitionException($"Alias cycle at {type}");
    }

    /// <summary>
    /// What a variant's tags are: the tag type decides, and an external tag (no tag type) is a string tag when a case
    /// gives its tag as text.
    /// </summary>
    public VariantTagKind TagKind(VariantDef variant) => variant.Tag switch
    {
        null => variant.Cases.Any(c => c.TagMember != null) ? VariantTagKind.String : VariantTagKind.Integer,
        NamedRef named when Find(named.Name) is EnumDef => VariantTagKind.Enum,
        var tag => Unalias(tag) is StringRef ? VariantTagKind.String : VariantTagKind.Integer,
    };

    public static FormatProject InMemory(IEnumerable<TypeDef> definitions, ProjectSettings? settings = null)
    {
        var project = new FormatProject(settings ?? new ProjectSettings(), System.IO.Directory.GetCurrentDirectory());
        foreach (var definition in definitions) project.Add(definition);
        return project;
    }

    /// <summary>The full path of a project file, or of the single <c>*.fstudio.yaml</c> in a directory.</summary>
    public static string ResolveFile(string path, FileSandbox? sandbox = null)
    {
        sandbox ??= FileSandbox.Unrestricted;
        path = sandbox.Check(path);
        if (!System.IO.Directory.Exists(path))
            return File.Exists(path) ? path : throw new FileNotFoundException($"{path} doesn't exist", path);
        var candidates = sandbox.EnumerateFiles(path, "*" + ProjectSettings.Extension).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FormatDefinitionException($"No *{ProjectSettings.Extension} in {path}"),
            _ => throw new FormatDefinitionException($"Several project files in {path}; pass one explicitly"),
        };
    }

    /// <summary>Loads a project file, or the single <c>*.fstudio.yaml</c> in a directory.</summary>
    /// <param name="natives">The native types every project has; null for the built-in ones.</param>
    /// <param name="plugins">Which plugins of native types may be loaded; null for all.</param>
    public static FormatProject Load(string path, FileSandbox? sandbox = null, NativeTypes? natives = null, PluginPolicy? plugins = null)
    {
        sandbox ??= FileSandbox.Unrestricted;
        path = ResolveFile(path, sandbox);
        var settings = ProjectSettings.Parse(File.ReadAllText(path), path);
        var project = new FormatProject(settings, Path.GetDirectoryName(path)!, path)
        {
            Sandbox = sandbox,
            Natives = natives ?? NativeTypes.BuiltIn,
            Plugins = plugins ?? PluginPolicy.All,
        };
        // Libraries and native types first: they decide which names in the sources are native types.
        LibraryLoader.LoadDependencies(project);
        project.LoadSources();
        project.LoadTests();
        Validation.Validator.Validate(project);
        return project;
    }

    /// <summary>Relative path (with forward slashes) of a file in the project.</summary>
    public string Relative(string file) => Path.GetRelativePath(Directory, file).Replace('\\', '/');

    /// <summary>
    /// The files matching each pattern in turn. In a restricted sandbox the patterns are matched against the files found
    /// inside the sandbox, so patterns like <c>../**</c> can't list or read anything outside it.
    /// </summary>
    public static IReadOnlyList<string> Glob(string directory, IEnumerable<string> patterns, IReadOnlyCollection<string> excludes,
        FileSandbox? sandbox = null)
    {
        var result = new List<string>();
        var files = sandbox is { IsRestricted: true } && System.IO.Directory.Exists(directory)
            ? sandbox.EnumerateFiles(directory, recursive: true).ToList()
            : null;
        foreach (var pattern in patterns)
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            foreach (var exclude in excludes) matcher.AddExclude(exclude);
            var matches = files == null
                ? matcher.GetResultsInFullPath(directory)
                : matcher.Match(directory, files).Files.Select(f => Path.GetFullPath(f.Path, directory));
            result.AddRange(matches.Order(StringComparer.Ordinal));
        }

        return result;
    }

    private void LoadSources()
    {
        var excludes = Settings.Sources.Where(s => s.StartsWith('!')).Select(s => s[1..]).ToList();
        excludes.Add("**/*" + ProjectSettings.Extension);
        // Test files are never definitions, also when a source pattern like the default **/*.yaml takes them in.
        excludes.AddRange(Settings.Tests);
        var origin = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var sources = Settings.Sources.Where(s => !s.StartsWith('!'))
            .SelectMany((pattern, index) => Glob(Directory, [pattern], excludes, Sandbox).Select(file => (File: file, Pattern: index + 1)))
            .Where(source => seenFiles.Add(source.File))
            .Select(source => (source.File, source.Pattern, Text: File.ReadAllText(source.File)))
            .ToList();

        // Generators first, which native types name in their codegen.
        var files = NativeFiles.Folder(Directory, Sandbox);
        foreach (var source in sources.Where(s => DeclaredGenerator.IsDeclaration(s.Text)))
        {
            if (DeclaredGenerator.Read(source.Text, Relative(source.File), files, Plugins, Diagnostics) is not { } generator) continue;
            if (DeclaredGenerators.FirstOrDefault(g => g.Name == generator.Name) is { } twice)
            {
                Diagnostics.Error($"Generator '{generator.Name}' is also declared in {twice.Source.File}", generator.Source);
                continue;
            }

            DeclaredGenerators.Add(generator);
        }

        AddGenerators(DeclaredGenerators);

        // Native type declarations next: references to them are told apart from type names by their names.
        foreach (var source in sources.Where(s => DeclaredNative.IsDeclaration(s.Text) && !DeclaredGenerator.IsDeclaration(s.Text)))
        {
            if (DeclaredNative.Read(source.Text, Relative(source.File), files, Plugins, Diagnostics, generators: Generators) is not { } native) continue;
            if (DeclaredNatives.FirstOrDefault(n => n.Name == native.Name) is { } twice)
            {
                Diagnostics.Error($"Native type '{native.Name}' is also declared in {twice.Source.File}", native.Source);
                continue;
            }

            DeclaredNatives.Add(native);
        }

        AddNatives(DeclaredNatives);

        // Code for native types, of the project's and its libraries' types.
        foreach (var source in sources.Where(s => NativeCodegenExtension.IsDeclaration(s.Text)))
        {
            if (NativeCodegenExtension.Read(source.Text, Relative(source.File), files, Diagnostics, generators: Generators) is { } extension)
                CodegenExtensions.Add(extension);
        }

        AddCodegen(CodegenExtensions);
        foreach (var (file, patternIndex, text) in sources.Where(s => !IsDeclaration(s.Text)))
        {
            var definition = DslReader.Read(text, Relative(file), Diagnostics);
            if (definition == null) continue;

            if (_types.TryGetValue(definition.Name, out var existing))
            {
                if (origin[definition.Name] == patternIndex)
                {
                    Diagnostics.Error($"'{definition.Name}' is also defined in {existing.Source?.File}", definition.Source);
                    continue;
                }

                Diagnostics.Info($"Overrides '{definition.Name}' from {existing.Source?.File}", definition.Source);
            }

            origin[definition.Name] = patternIndex;
            Add(definition);
        }
    }

    /// <summary>Whether a source file declares a native type, code for one or a code generator rather than a definition.</summary>
    public static bool IsDeclaration(string text) =>
        DeclaredNative.IsDeclaration(text) || DeclaredGenerator.IsDeclaration(text) || NativeCodegenExtension.IsDeclaration(text);

    private void LoadTests()
    {
        foreach (var file in Glob(Directory, Settings.Tests, [], Sandbox))
        {
            var relative = Relative(file);
            try
            {
                Tests.AddRange(TestFiles.Load(File.ReadAllText(file), relative, Diagnostics));
            }
            catch (YamlDotNet.Core.YamlException e)
            {
                Diagnostics.Error(e.InnerException?.Message ?? e.Message, new SourceLocation(relative, (int)e.Start.Line, (int)e.Start.Column));
            }
            catch (Exception e) when (e is FormatDefinitionException or InvalidOperationException)
            {
                Diagnostics.Error(e.Message, new SourceLocation(relative));
            }
        }
    }

    /// <summary>Whether the test patterns include a file of the project, i.e. its test cases are loaded when it exists.</summary>
    public bool IsTestFile(string file)
    {
        var relative = Relative(Path.GetFullPath(file, Directory));
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") return false;
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(Settings.Tests);
        return matcher.Match(relative).HasMatches;
    }

    /// <summary>
    /// The file new test cases go to by default: the first file with test cases, or <c>tests.yaml</c> in the folder of
    /// the first test pattern. Null when the project has no test patterns.
    /// </summary>
    public string? DefaultTestFile()
    {
        if (Tests.FirstOrDefault()?.Source?.File is { } existing) return existing;
        foreach (var pattern in Settings.Tests)
        {
            var wildcard = pattern.IndexOfAny(['*', '?', '[']);
            var folder = (wildcard < 0 ? Path.GetDirectoryName(pattern) ?? "" : pattern[..wildcard]).TrimEnd('/', '\\');
            var file = folder.Length > 0 ? folder + "/tests.yaml" : "tests.yaml";
            if (IsTestFile(file)) return file;
        }

        return null;
    }

    /// <summary>
    /// Writes a test case to <paramref name="file"/> (relative to the project), replacing <paramref name="original"/> when
    /// it is in that file, or adding it at the end. An original in another file is removed from there.
    /// </summary>
    public string SaveTest(TestCaseDef test, string file, TestCaseDef? original = null)
    {
        var path = Sandbox.Check(Path.GetFullPath(file, Directory));
        if (!path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            throw new FormatDefinitionException($"{file} isn't a .yaml file");
        if (!IsTestFile(path))
        {
            throw new FormatDefinitionException(Settings.Tests.Count == 0
                ? "The project has no test patterns; add e.g. 'tests: [tests/**/*.yaml]' to the project file"
                : $"{Relative(path)} isn't matched by the project's test patterns ({string.Join(", ", Settings.Tests)}), so it wouldn't be loaded");
        }

        var sameFile = original?.Source?.File is { } originalFile && Path.GetFullPath(originalFile, Directory) == path;
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        if (sameFile) CheckUnchanged(text, original!);
        var updated = TestFiles.Replace(text, sameFile ? original!.Index : null, test);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
        if (original != null && !sameFile) DeleteTest(original);
        return path;
    }

    /// <summary>Removes a test case from its file, and deletes the file when no test case is left.</summary>
    public void DeleteTest(TestCaseDef test)
    {
        var path = Sandbox.Check(Path.GetFullPath(test.Source?.File ?? throw new FormatDefinitionException($"'{test.Name}' has no file"), Directory));
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        CheckUnchanged(text, test);
        var updated = TestFiles.Replace(text, test.Index, null);
        if (updated == null) File.Delete(path);
        else File.WriteAllText(path, updated);
    }

    /// <summary>The full path of a test case's <c>file</c>, which is relative to the test file.</summary>
    /// <summary>Writes new expected results into the case's file, keeping the rest of the file as it is.</summary>
    public void UpdateTest(TestCaseDef test, TestUpdate update)
    {
        var path = Sandbox.Check(Path.GetFullPath(test.Source?.File ?? throw new FormatDefinitionException($"'{test.Name}' has no file"), Directory));
        var text = File.ReadAllText(path);
        CheckUnchanged(text, test);
        var updated = TestFiles.Update(text, test.Index, update);
        // An edit of the text that breaks the file is refused rather than written.
        var diagnostics = new DiagnosticBag();
        try
        {
            var cases = TestFiles.Load(updated, Relative(path), diagnostics);
            if (diagnostics.HasErrors || test.Index >= cases.Count || cases[test.Index].Name != test.Name) throw new FormatDefinitionException("the case can't be read back");
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or FormatDefinitionException)
        {
            throw new FormatDefinitionException($"Not updating {test.Name}: the new text of {Relative(path)} would be broken ({e.Message})");
        }

        File.WriteAllText(path, updated);
    }

    public string TestFilePath(TestCaseDef test)
    {
        var testFile = Path.GetFullPath(test.Source?.File ?? throw new FormatDefinitionException($"'{test.Name}' has no file"), Directory);
        return Sandbox.Check(Path.GetFullPath(test.File ?? throw new FormatDefinitionException($"'{test.Name}' has no 'file'"),
            Path.GetDirectoryName(testFile)!));
    }

    /// <summary>The bytes a test case gives, from its hex or its file; null when it gives none.</summary>
    public byte[]? TestBytes(TestCaseDef test) =>
        test.Hex != null && !test.HexAuto ? Simulation.Hex.Parse(test.Hex) : test.File != null ? File.ReadAllBytes(TestFilePath(test)) : null;

    /// <summary>Makes sure the test case is still at its index, so that a file changed since loading isn't overwritten blindly.</summary>
    private static void CheckUnchanged(string? text, TestCaseDef test)
    {
        var entries = text == null ? [] : TestFiles.Entries(YamlValues.ParseYaml(text));
        if (test.Index >= entries.Count || TestFiles.NameOf(entries[test.Index]) is { } name && name != test.Name)
            throw new FormatDefinitionException($"{test.Source?.File} changed since it was loaded; reload the project and try again");
    }

    /// <summary>
    /// The file a definition is saved to: its source file, or for new ones <c>&lt;folder&gt;/&lt;Name&gt;.yaml</c> in the
    /// folder of its kind (<see cref="KindFolder"/>). If the sources don't load that folder, the kind folder goes below
    /// the folder of a source pattern (or into that folder), e.g. <c>types/enums</c> for <c>sources: [types/**/*.yaml, ...]</c>.
    /// </summary>
    public string FileFor(TypeDef definition)
    {
        if (definition.Source?.File is { } file && file != "<input>") return Path.Combine(Directory, file);

        var kindFolder = KindFolder(definition.Kind);
        var name = definition.Name + ".yaml";
        var candidates = Settings.Sources.Where(s => !s.StartsWith('!'))
            .Select(pattern => pattern.IndexOfAny(['*', '?', '[']) is var wildcard and >= 0 ? pattern[..wildcard] : Path.GetDirectoryName(pattern) ?? "")
            .Select(prefix => prefix.TrimEnd('/', '\\'))
            .Where(prefix => prefix.Length > 0 && !prefix.Contains(".."))
            .SelectMany(prefix => Path.GetFileName(prefix) == kindFolder ? [prefix] : new[] { Path.Combine(prefix, kindFolder), prefix })
            .Prepend(kindFolder)
            .Select(folder => Path.Combine(Directory, folder, name));
        return candidates.FirstOrDefault(IsSource) ?? Path.Combine(Directory, kindFolder, name);
    }

    /// <summary>The folder for new definitions of a kind: <c>packets</c>, <c>types</c> for structs, otherwise the kind's plural.</summary>
    public static string KindFolder(string kind) => kind switch
    {
        "packet" => "packets",
        "struct" => "types",
        "alias" => "aliases",
        _ => kind + "s",
    };

    /// <summary>Whether the source patterns include a file of the project, i.e. it is loaded as a definition when it exists.</summary>
    public bool IsSource(string file)
    {
        var relative = Relative(Path.GetFullPath(file, Directory));
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") return false;
        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var pattern in Settings.Sources)
        {
            if (pattern.StartsWith('!')) matcher.AddExclude(pattern[1..]);
            else matcher.AddInclude(pattern);
        }
        matcher.AddExclude("**/*" + ProjectSettings.Extension);
        return matcher.Match(relative).HasMatches;
    }

    /// <summary>
    /// Writes the general settings (<see cref="ProjectSettings.GeneralKeys"/>: name, kind, defaults, dependencies, sources,
    /// ...) of <paramref name="settings"/> to the project file and returns them as read back. The other sections stay as
    /// they are (see <see cref="ProjectSettings.ReplaceGeneral"/>); <see cref="Settings"/> doesn't change, the project
    /// has to be loaded again.
    /// </summary>
    public ProjectSettings SaveSettings(ProjectSettings settings)
    {
        var file = Sandbox.Check(FilePath ?? throw new FormatDefinitionException("The project has no file"));
        if (string.IsNullOrWhiteSpace(settings.Name)) throw new FormatDefinitionException("The project needs a name");
        if (settings.Sources.Count == 0) throw new FormatDefinitionException("The project needs at least one source pattern");
        if (settings.Dependencies.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.Library)) != null)
            throw new FormatDefinitionException("Every dependency needs a library name");

        var updated = ProjectSettings.ReplaceGeneral(File.ReadAllText(file), settings);
        var parsed = ProjectSettings.Parse(updated, file);
        File.WriteAllText(file, updated);
        return parsed;
    }

    /// <summary>
    /// Writes the codegen targets to the project file; only its <c>codegen</c> section changes (see
    /// <see cref="ProjectSettings.ReplaceCodegen"/>).
    /// </summary>
    public void SaveCodegen(IReadOnlyList<CodegenTarget> targets)
    {
        var file = Sandbox.Check(FilePath ?? throw new FormatDefinitionException("The project has no file"));
        foreach (var target in targets) CodeGeneration.Check(this, target);
        if (targets.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new FormatDefinitionException($"Two codegen targets are named '{duplicate.Key}'");

        var updated = ProjectSettings.ReplaceCodegen(File.ReadAllText(file), targets);
        var parsed = ProjectSettings.Parse(updated, file);
        if (parsed.Codegen.Count != targets.Count) throw new FormatDefinitionException("The codegen section of the project file couldn't be updated");
        File.WriteAllText(file, updated);
        Settings.Codegen = parsed.Codegen;
    }

    /// <summary>
    /// Writes the protocols to the project file, keeping the rest of it (see <see cref="ProjectSettings.ReplaceProtocols"/>).
    /// </summary>
    public void SaveProtocols(IReadOnlyList<ProtocolDef> protocols)
    {
        var file = Sandbox.Check(FilePath ?? throw new FormatDefinitionException("The project has no file"));
        foreach (var protocol in protocols)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(protocol.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new FormatDefinitionException($"Protocol name '{protocol.Name}' is not an identifier");
            if (protocol.Groups.Any(g => !System.Text.RegularExpressions.Regex.IsMatch(g, @"^[^\s,\[\]{}#:]+$")))
                throw new FormatDefinitionException($"Protocol {protocol.Name}: groups can't be empty or contain spaces, commas, brackets, '#' or ':'");
        }

        if (protocols.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new FormatDefinitionException($"Two protocols are named '{duplicate.Key}'");

        var updated = ProjectSettings.ReplaceProtocols(File.ReadAllText(file), protocols);
        var parsed = ProjectSettings.Parse(updated, file);
        if (parsed.Protocols.Count != protocols.Count) throw new FormatDefinitionException("The protocols section of the project file couldn't be updated");
        File.WriteAllText(file, updated);
        Settings.Protocols = parsed.Protocols;
    }

    /// <summary>Writes a definition back to its file and replaces it in the project.</summary>
    public string Save(TypeDef definition)
    {
        var file = Sandbox.Check(FileFor(definition));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DslWriter.Write(definition));
        definition.Source ??= new SourceLocation(Relative(file), 1, 1);
        Add(definition);
        return file;
    }
}
