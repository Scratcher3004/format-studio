using System.Text.Json.Nodes;
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
    public static IReadOnlyList<string> Names { get; } = ["endian", "length", "string"];

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

public sealed class SampleDef
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public JsonNode? Value { get; set; }
    /// <summary>The expected encoding, if known.</summary>
    public string? Hex { get; set; }
    public SourceLocation? Source { get; set; }
    /// <summary>The position of the sample among the samples of its file.</summary>
    public int Index { get; set; }
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
    public List<string> Samples { get; set; } = [];
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
        if (root["samples"] is JsonArray samples) settings.Samples = samples.Select(s => s!.GetValue<string>()).ToList();
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

    public string ToYaml()
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
        map.AddIf("samples", Samples.Cast<object?>().ToList());
        if (Import.Count > 0) map.Add("import", Import);
        map.AddIf("protocols", ProtocolsYaml(Protocols));
        map.AddIf("codegen", CodegenYaml(Codegen));
        return YamlEmitter.Write(map);
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

    private static string ReplaceSection(string text, string key, List<object?> items)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var section = items.Count == 0
            ? []
            : YamlEmitter.Write(new YMap { { key, items } }).TrimEnd('\n').Split('\n');
        var start = lines.FindIndex(line => System.Text.RegularExpressions.Regex.IsMatch(line, $@"^{key}\s*:"));
        if (start < 0)
        {
            if (section.Length == 0) return text;
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(section);
            lines.Add("");
            return string.Join(newline, lines);
        }

        // The section goes on while lines are indented, empty or list items at the margin ("- name: ...").
        var end = start + 1;
        while (end < lines.Count && (lines[end].Length == 0 || lines[end][0] is ' ' or '\t' || lines[end].StartsWith('-') && !lines[end].StartsWith("---")))
            end++;
        while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, section);
        return string.Join(newline, lines);
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
    public List<SampleDef> Samples { get; } = [];
    /// <summary>The native types the project's definitions can use: built-in, its own and its libraries'.</summary>
    public NativeTypes Natives { get; set; } = NativeTypes.BuiltIn;
    /// <summary>The native types the project declares itself (<c>native:</c> files among its sources).</summary>
    public List<DeclaredNative> DeclaredNatives { get; } = [];
    /// <summary>Which plugins of native types may be loaded, the project's and its libraries'.</summary>
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
    /// can't tell <c>rgb</c> from the name of a definition. Native types take precedence.
    /// </summary>
    public TypeRef Normalize(TypeRef type) => type switch
    {
        NamedRef { As: null, Tag: null } named when Natives.Find(named.Name) != null => new NativeRef(named.Name),
        ArrayRef array => array with { Element = Normalize(array.Element) },
        MapRef map => map with { Key = Normalize(map.Key), Value = Normalize(map.Value) },
        OptionalRef optional => new OptionalRef(Normalize(optional.Inner)),
        SizedRef sized => sized with { Inner = Normalize(sized.Inner) },
        _ => type,
    };

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
        var copy = new FormatProject(Settings, Directory, FilePath) { Sandbox = Sandbox, Natives = Natives, Plugins = Plugins };
        copy.DeclaredNatives.AddRange(DeclaredNatives);
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

    /// <summary>The native type of a reference, with its arguments completed from the project's defaults.</summary>
    public BoundNative Bind(NativeRef native) => Natives.Bind(native, Defaults);

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
        project.LoadSamples();
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
        var origin = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var sources = Settings.Sources.Where(s => !s.StartsWith('!'))
            .SelectMany((pattern, index) => Glob(Directory, [pattern], excludes, Sandbox).Select(file => (File: file, Pattern: index + 1)))
            .Where(source => seenFiles.Add(source.File))
            .Select(source => (source.File, source.Pattern, Text: File.ReadAllText(source.File)))
            .ToList();

        // Native type declarations first: references to them are told apart from type names by their names.
        var files = NativeFiles.Folder(Directory, Sandbox);
        foreach (var source in sources.Where(s => DeclaredNative.IsDeclaration(s.Text)))
        {
            if (DeclaredNative.Read(source.Text, Relative(source.File), files, Plugins, Diagnostics) is not { } native) continue;
            if (DeclaredNatives.FirstOrDefault(n => n.Name == native.Name) is { } twice)
            {
                Diagnostics.Error($"Native type '{native.Name}' is also declared in {twice.Source.File}", native.Source);
                continue;
            }

            DeclaredNatives.Add(native);
        }

        AddNatives(DeclaredNatives);
        foreach (var (file, patternIndex, text) in sources.Where(s => !DeclaredNative.IsDeclaration(s.Text)))
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

    private void LoadSamples()
    {
        foreach (var file in Glob(Directory, Settings.Samples, [], Sandbox))
        {
            var relative = Relative(file);
            try
            {
                var entries = SampleFiles.Entries(YamlValues.ParseYaml(File.ReadAllText(file)));
                for (var index = 0; index < entries.Count; index++)
                    Samples.Add(SampleFiles.Read(entries[index], Path.GetFileNameWithoutExtension(file), relative, index));
            }
            catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                Diagnostics.Error(e.Message, new SourceLocation(relative));
            }
        }
    }

    /// <summary>Whether the sample patterns include a file of the project, i.e. its samples are loaded when it exists.</summary>
    public bool IsSampleFile(string file)
    {
        var relative = Relative(Path.GetFullPath(file, Directory));
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") return false;
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(Settings.Samples);
        return matcher.Match(relative).HasMatches;
    }

    /// <summary>
    /// The file new samples go to by default: the first file with samples, or <c>samples.yaml</c> in the folder of the
    /// first sample pattern. Null when the project has no sample patterns.
    /// </summary>
    public string? DefaultSampleFile()
    {
        if (Samples.FirstOrDefault()?.Source?.File is { } existing) return existing;
        foreach (var pattern in Settings.Samples)
        {
            var wildcard = pattern.IndexOfAny(['*', '?', '[']);
            var folder = (wildcard < 0 ? Path.GetDirectoryName(pattern) ?? "" : pattern[..wildcard]).TrimEnd('/', '\\');
            var file = folder.Length > 0 ? folder + "/samples.yaml" : "samples.yaml";
            if (IsSampleFile(file)) return file;
        }

        return null;
    }

    /// <summary>
    /// Writes a sample to <paramref name="file"/> (relative to the project), replacing <paramref name="original"/> when it
    /// is in that file, or adding it at the end. An original in another file is removed from there.
    /// </summary>
    public string SaveSample(SampleDef sample, string file, SampleDef? original = null)
    {
        var path = Sandbox.Check(Path.GetFullPath(file, Directory));
        if (!path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            throw new FormatDefinitionException($"{file} isn't a .yaml file");
        if (!IsSampleFile(path))
        {
            throw new FormatDefinitionException(Settings.Samples.Count == 0
                ? "The project has no sample patterns; add e.g. 'samples: [samples/**/*.yaml]' to the project file"
                : $"{Relative(path)} isn't matched by the project's sample patterns ({string.Join(", ", Settings.Samples)}), so it wouldn't be loaded");
        }

        var sameFile = original?.Source?.File is { } originalFile && Path.GetFullPath(originalFile, Directory) == path;
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        if (sameFile) CheckUnchanged(text, original!);
        var updated = SampleFiles.Replace(text, sameFile ? original!.Index : null, sample);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
        if (original != null && !sameFile) DeleteSample(original);
        return path;
    }

    /// <summary>Removes a sample from its file, and deletes the file when no sample is left.</summary>
    public void DeleteSample(SampleDef sample)
    {
        var path = Sandbox.Check(Path.GetFullPath(sample.Source?.File ?? throw new FormatDefinitionException($"'{sample.Name}' has no file"), Directory));
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        CheckUnchanged(text, sample);
        var updated = SampleFiles.Replace(text, sample.Index, null);
        if (updated == null) File.Delete(path);
        else File.WriteAllText(path, updated);
    }

    /// <summary>Makes sure the sample is still at its index, so that a file changed since loading isn't overwritten blindly.</summary>
    private static void CheckUnchanged(string? text, SampleDef sample)
    {
        var entries = text == null ? [] : SampleFiles.Entries(YamlValues.ParseYaml(text));
        if (sample.Index >= entries.Count || SampleFiles.NameOf(entries[sample.Index]) is { } name && name != sample.Name)
            throw new FormatDefinitionException($"{sample.Source?.File} changed since it was loaded; reload the project and try again");
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
    /// Writes the codegen targets to the project file; only its <c>codegen</c> section changes (see
    /// <see cref="ProjectSettings.ReplaceCodegen"/>).
    /// </summary>
    public void SaveCodegen(IReadOnlyList<CodegenTarget> targets)
    {
        var file = Sandbox.Check(FilePath ?? throw new FormatDefinitionException("The project has no file"));
        foreach (var target in targets) Codegen.CodeGeneration.Check(target);
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
