using System.Text.Json.Nodes;
using FormatStudio.Dsl;
using FormatStudio.Model;
using Microsoft.Extensions.FileSystemGlobbing;

namespace FormatStudio.Projects;

/// <summary>Project-wide defaults for everything a type expression leaves open.</summary>
public sealed class FormatDefaults
{
    public Endian Endian { get; set; } = Endian.Little;
    /// <summary>The prefix of strings, bytes, arrays and maps with a default length.</summary>
    public PrimitiveRef Length { get; set; } = new(PrimitiveKind.VarUInt32);
    public StringEncoding String { get; set; } = StringEncoding.Utf8;
    /// <summary>The flavor of <c>nbt</c> without one. There is no built-in default; using NBT without either is an error.</summary>
    public NbtFlavor? Nbt { get; set; }

    public NbtFlavor NbtFlavorOf(NbtRef nbt) => nbt.Flavor ?? Nbt
        ?? throw new FormatDefinitionException("NBT without a flavor: set defaults.nbt in the project or write {nbt: <flavor>}");
}

public sealed class CodegenTarget
{
    public required string Name { get; set; }
    public required string Generator { get; set; }
    /// <summary>Output folder, relative to the project directory.</summary>
    public required string Output { get; set; }
    public JsonObject Options { get; set; } = [];
}

public sealed class SampleDef
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public JsonNode? Value { get; set; }
    /// <summary>The expected encoding, if known.</summary>
    public string? Hex { get; set; }
    public SourceLocation? Source { get; set; }
}

/// <summary>The contents of a <c>*.fstudio.yaml</c> project file.</summary>
public sealed class ProjectSettings
{
    public const string Extension = ".fstudio.yaml";

    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public string? Version { get; set; }
    public FormatDefaults Defaults { get; set; } = new();
    /// <summary>Globs of definition files. A type in a later glob replaces one with the same name in an earlier glob.</summary>
    public List<string> Sources { get; set; } = ["**/*.yaml", $"!**/*{Extension}"];
    public List<string> Samples { get; set; } = [];
    /// <summary>Importer settings, keyed by importer name.</summary>
    public JsonObject Import { get; set; } = [];
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
            if (defaults["nbt"]?.GetValue<string>() is { } flavor)
                settings.Defaults.Nbt = Wrap(() => TypeExpressions.ParseFlavor(flavor), file);
        }

        if (root["sources"] is JsonArray sources) settings.Sources = sources.Select(s => s!.GetValue<string>()).ToList();
        if (root["samples"] is JsonArray samples) settings.Samples = samples.Select(s => s!.GetValue<string>()).ToList();
        if (root["import"] is JsonObject import) settings.Import = (JsonObject)import.DeepClone();
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
        var defaults = new YMap
        {
            { "endian", Defaults.Endian == Endian.Little ? "little" : "big" },
            { "length", Defaults.Length.ToString() },
            { "string", TypeExpressions.EncodingName(Defaults.String) },
        };
        if (Defaults.Nbt is { } nbt) defaults.Add("nbt", TypeExpressions.FlavorName(nbt));
        map.Add("defaults", defaults);
        map.Add("sources", Sources.Cast<object?>().ToList());
        map.AddIf("samples", Samples.Cast<object?>().ToList());
        if (Import.Count > 0) map.Add("import", Import);
        map.AddIf("codegen", Codegen.Select(target =>
        {
            var targetMap = new YMap { { "name", target.Name }, { "generator", target.Generator }, { "output", target.Output } };
            if (target.Options.Count > 0) targetMap.Add("options", target.Options);
            return (object?)targetMap;
        }).ToList());
        return YamlEmitter.Write(map);
    }
}

/// <summary>A loaded project: settings plus all definitions, keyed by name.</summary>
public sealed class FormatProject
{
    private readonly Dictionary<string, TypeDef> _types = new(StringComparer.Ordinal);

    public ProjectSettings Settings { get; }
    /// <summary>The project file, or null for an in-memory project.</summary>
    public string? FilePath { get; }
    public string Directory { get; }
    public DiagnosticBag Diagnostics { get; } = [];
    public List<SampleDef> Samples { get; } = [];

    public FormatProject(ProjectSettings settings, string directory, string? filePath = null)
    {
        Settings = settings;
        Directory = directory;
        FilePath = filePath;
    }

    public FormatDefaults Defaults => Settings.Defaults;
    public IReadOnlyDictionary<string, TypeDef> Types => _types;
    public IEnumerable<StructDef> Packets => _types.Values.OfType<StructDef>().Where(s => s.IsPacket);

    public TypeDef? Find(string name) => _types.GetValueOrDefault(name);

    public void Add(TypeDef definition) => _types[definition.Name] = definition;

    public bool Remove(string name) => _types.Remove(name);

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
    public static string ResolveFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!System.IO.Directory.Exists(path))
            return File.Exists(path) ? path : throw new FileNotFoundException($"{path} doesn't exist", path);
        var candidates = System.IO.Directory.GetFiles(path, "*" + ProjectSettings.Extension);
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FormatDefinitionException($"No *{ProjectSettings.Extension} in {path}"),
            _ => throw new FormatDefinitionException($"Several project files in {path}; pass one explicitly"),
        };
    }

    /// <summary>Loads a project file, or the single <c>*.fstudio.yaml</c> in a directory.</summary>
    public static FormatProject Load(string path)
    {
        path = ResolveFile(path);
        var settings = ProjectSettings.Parse(File.ReadAllText(path), path);
        var project = new FormatProject(settings, Path.GetDirectoryName(path)!, path);
        project.LoadSources();
        project.LoadSamples();
        Validation.Validator.Validate(project);
        return project;
    }

    /// <summary>Relative path (with forward slashes) of a file in the project.</summary>
    public string Relative(string file) => Path.GetRelativePath(Directory, file).Replace('\\', '/');

    public static IReadOnlyList<string> Glob(string directory, IEnumerable<string> patterns, IReadOnlyCollection<string> excludes)
    {
        var result = new List<string>();
        foreach (var pattern in patterns)
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            foreach (var exclude in excludes) matcher.AddExclude(exclude);
            result.AddRange(matcher.GetResultsInFullPath(directory).Order(StringComparer.Ordinal));
        }

        return result;
    }

    private void LoadSources()
    {
        var excludes = Settings.Sources.Where(s => s.StartsWith('!')).Select(s => s[1..]).ToList();
        excludes.Add("**/*" + ProjectSettings.Extension);
        var origin = new Dictionary<string, int>(StringComparer.Ordinal);
        var patternIndex = 0;
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in Settings.Sources.Where(s => !s.StartsWith('!')))
        {
            patternIndex++;
            foreach (var file in Glob(Directory, [pattern], excludes))
            {
                if (!seenFiles.Add(file)) continue;
                var definition = DslReader.Read(File.ReadAllText(file), Relative(file), Diagnostics);
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
                _types[definition.Name] = definition;
            }
        }
    }

    private void LoadSamples()
    {
        foreach (var file in Glob(Directory, Settings.Samples, []))
        {
            var relative = Relative(file);
            try
            {
                var root = YamlValues.ParseYaml(File.ReadAllText(file));
                IEnumerable<JsonObject> entries = root switch
                {
                    JsonArray array => array.OfType<JsonObject>(),
                    JsonObject obj when obj["samples"] is JsonArray array => array.OfType<JsonObject>(),
                    JsonObject obj => [obj],
                    _ => [],
                };
                foreach (var entry in entries)
                {
                    Samples.Add(new SampleDef
                    {
                        Name = entry["sample"]?.ToString() ?? entry["name"]?.ToString() ?? Path.GetFileNameWithoutExtension(file),
                        Type = entry["type"]?.ToString() ?? throw new FormatDefinitionException("A sample needs a 'type'"),
                        Value = entry["value"]?.DeepClone(),
                        Hex = entry["hex"]?.ToString(),
                        Source = new SourceLocation(relative),
                    });
                }
            }
            catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                Diagnostics.Error(e.Message, new SourceLocation(relative));
            }
        }
    }

    /// <summary>The file a definition is saved to: its source file, or <c>&lt;kind&gt;s/&lt;Name&gt;.yaml</c> for new ones.</summary>
    public string FileFor(TypeDef definition) => definition.Source?.File is { } file && file != "<input>"
        ? Path.Combine(Directory, file)
        : Path.Combine(Directory, definition.Kind switch
        {
            "packet" => "packets",
            "struct" => "types",
            _ => definition.Kind + "s",
        }, definition.Name + ".yaml");

    /// <summary>Writes a definition back to its file and replaces it in the project.</summary>
    public string Save(TypeDef definition)
    {
        var file = FileFor(definition);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DslWriter.Write(definition));
        definition.Source ??= new SourceLocation(Relative(file), 1, 1);
        _types[definition.Name] = definition;
        return file;
    }
}
