using System.IO.Compression;
using System.Reflection;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Natives;

namespace FormatStudio.Projects;

/// <summary>
/// A packed library (<c>&lt;name&gt;-&lt;version&gt;.fslib</c>): a zip with the library's project file, its definition
/// files and native type declarations with the files they use (plugins, runtime files), at the paths they have in the
/// library project.
/// </summary>
public sealed class LibraryPackage
{
    public const string Extension = ".fslib";

    public required ProjectSettings Settings { get; init; }
    /// <summary>The definition and native declaration files: path inside the package, text.</summary>
    public required IReadOnlyList<(string Path, string Text)> Files { get; init; }
    /// <summary>Every file but the project file, by path inside the package.</summary>
    public IReadOnlyDictionary<string, byte[]> Resources { get; init; } = new Dictionary<string, byte[]>();
    /// <summary>Where the package comes from, e.g. its file or <c>built-in</c>; shown in messages.</summary>
    public required string Origin { get; init; }

    public string Name => Settings.Name;
    public string? Version => Settings.Version;

    public override string ToString() => Version == null ? Name : $"{Name} {Version}";

    public static LibraryPackage Read(Stream stream, string origin)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            string Text(ZipArchiveEntry entry)
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }

            var projectFiles = zip.Entries.Where(e => !e.FullName.Contains('/') && e.FullName.EndsWith(ProjectSettings.Extension, StringComparison.Ordinal)).ToList();
            if (projectFiles.Count != 1)
                throw new FormatDefinitionException($"{origin}: a library package needs exactly one {ProjectSettings.Extension} at its root");
            var settings = ProjectSettings.Parse(Text(projectFiles[0]), $"{origin}!/{projectFiles[0].FullName}");
            var files = zip.Entries
                .Where(e => e != projectFiles[0] && e.FullName.EndsWith(".yaml", StringComparison.Ordinal))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .Select(e => (e.FullName, Text(e)))
                .ToList();
            var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries.Where(e => e != projectFiles[0] && !e.FullName.EndsWith('/')))
            {
                using var content = entry.Open();
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                resources[entry.FullName] = buffer.ToArray();
            }

            return new LibraryPackage { Settings = settings, Files = files, Resources = resources, Origin = origin };
        }
        catch (InvalidDataException e)
        {
            throw new FormatDefinitionException($"{origin} is not a library package: {e.Message}");
        }
    }

    /// <summary>The file name of a package: <c>&lt;name&gt;-&lt;version&gt;.fslib</c>.</summary>
    public static string FileName(string name, string version) => $"{name}-{version}{Extension}";

    /// <summary>
    /// Packs a library project into <paramref name="outputDirectory"/> and returns the package's path. The project must be
    /// a valid library with a version; only the definitions it ends up with are packed (not those overridden by later
    /// sources), with its native type declarations and their files; samples and code generation settings stay out.
    /// </summary>
    public static string Pack(FormatProject library, string outputDirectory)
    {
        var settings = library.Settings;
        if (settings.Kind != ProjectKind.Library) throw new FormatDefinitionException($"{settings.Name} is not a library (kind: library)");
        if (settings.Version is not { Length: > 0 } version || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(' '))
            throw new FormatDefinitionException($"A library needs a version to be packed, and it must be usable in a file name");
        if (library.FilePath == null) throw new FormatDefinitionException("Only a library loaded from a project file can be packed");
        if (library.Diagnostics.HasErrors)
            throw new FormatDefinitionException($"{settings.Name} has errors:\n{string.Join("\n", library.Diagnostics.Where(d => d.Severity == Severity.Error))}");

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName(settings.Name, version));
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var added = new HashSet<string>(StringComparer.Ordinal);
        void Add(string entryName, string what)
        {
            if (entryName.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(entryName))
                throw new FormatDefinitionException($"{what} is outside the library folder ({entryName})");
            if (!added.Add(entryName)) return;
            using var output = zip.CreateEntry(entryName).Open();
            using var input = File.OpenRead(library.Sandbox.Check(Path.Combine(library.Directory, entryName)));
            input.CopyTo(output);
        }

        Add(Path.GetFileName(library.FilePath), "The project file");
        foreach (var definition in library.Types.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
            Add(definition.Source?.File ?? throw new FormatDefinitionException($"{definition.Name} has no file"), definition.Name);
        foreach (var native in library.DeclaredNatives.OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            Add(native.Source.File, native.Name);
            foreach (var file in native.Files) Add(file, native.Name);
        }

        foreach (var generator in library.DeclaredGenerators.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            Add(generator.Source.File, generator.Name);
            foreach (var file in generator.Files) Add(file, generator.Name);
        }

        foreach (var extension in library.CodegenExtensions.OrderBy(e => e.Source.File, StringComparer.Ordinal))
        {
            Add(extension.Source.File, $"native-codegen for {extension.Type}");
            foreach (var file in extension.Files) Add(file, $"native-codegen for {extension.Type}");
        }

        return path;
    }

    /// <summary>Orders versions by their dot-separated parts, numerically where both parts are numbers.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (a == null || b == null) return a == null ? b == null ? 0 : -1 : 1;
        var left = a.Split('.', '-');
        var right = b.Split('.', '-');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;
            var order = long.TryParse(left[i], out var x) && long.TryParse(right[i], out var y)
                ? x.CompareTo(y)
                : string.CompareOrdinal(left[i], right[i]);
            if (order != 0) return order;
        }

        return 0;
    }
}

/// <summary>
/// Where a project finds library packages: the <c>.fslib</c> files in its library folders, then the built-in libraries
/// (<c>Libraries/*.fslib</c> in this project, embedded in the assembly).
/// </summary>
public sealed class LibraryCatalog(IReadOnlyList<string> folders, FileSandbox sandbox, IReadOnlyList<LibraryPackage>? builtins = null)
{
    private static readonly Lazy<IReadOnlyList<LibraryPackage>> BuiltinPackages = new(() =>
    {
        var assembly = typeof(LibraryCatalog).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(LibraryPackage.Extension, StringComparison.Ordinal))
            .Select(n => LibraryPackage.Read(assembly.GetManifestResourceStream(n)!, $"built-in {n}"))
            .ToList();
    });

    /// <summary>The libraries that come with Format Studio.</summary>
    public static IReadOnlyList<LibraryPackage> Builtins => BuiltinPackages.Value;

    public static LibraryCatalog For(FormatProject project) => new(
        project.Settings.Libraries.Select(folder => Path.GetFullPath(folder, project.Directory)).ToList(), project.Sandbox);

    /// <summary>The folders searched, for messages.</summary>
    public IReadOnlyList<string> Folders => folders;

    /// <summary>Every package of the given library: those in the folders first, then the built-in ones.</summary>
    public IEnumerable<LibraryPackage> Candidates(string name) => Packages($"{name}-*{LibraryPackage.Extension}").Where(p => p.Name == name);

    /// <summary>
    /// Every package in the folders, then the built-in ones, e.g. to pick a dependency from. Packages that can't be read
    /// are left out.
    /// </summary>
    public IEnumerable<LibraryPackage> All() => Packages("*" + LibraryPackage.Extension, skipUnreadable: true);

    private IEnumerable<LibraryPackage> Packages(string pattern, bool skipUnreadable = false)
    {
        foreach (var folder in folders)
        {
            if (!sandbox.Contains(folder) || !Directory.Exists(folder)) continue;
            foreach (var file in sandbox.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal))
            {
                LibraryPackage package;
                try
                {
                    using var stream = File.OpenRead(sandbox.Check(file));
                    package = LibraryPackage.Read(stream, Path.GetFileName(file));
                }
                catch (Exception e) when (skipUnreadable && e is FormatDefinitionException or IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    continue;
                }

                yield return package;
            }
        }

        foreach (var package in builtins ?? Builtins) yield return package;
    }

    /// <summary>The package with the exact <paramref name="version"/>, or the highest version when it is null.</summary>
    public LibraryPackage? Find(string name, string? version)
    {
        var candidates = Candidates(name).ToList();
        return version != null
            ? candidates.FirstOrDefault(p => p.Version == version)
            : candidates.OrderByDescending(p => p.Version, Comparer<string?>.Create(LibraryPackage.CompareVersions)).FirstOrDefault();
    }
}

/// <summary>
/// Loads the libraries of a project (dependencies of dependencies included) into it as modules. Each library is checked
/// on its own, then baked: its names are qualified (<c>Library.Type</c>) and its defaults written into its types, except
/// those it inherits, so that its definitions mean the same in every project.
/// </summary>
public sealed class LibraryLoader
{
    private readonly FormatProject _project;
    private readonly LibraryCatalog _catalog;
    // The native types every project has (built-in, given by the host); a library adds its own and its dependencies'.
    private readonly NativeTypes _natives;

    private LibraryLoader(FormatProject project, LibraryCatalog catalog)
    {
        _project = project;
        _catalog = catalog;
        _natives = project.Natives;
    }

    /// <summary>
    /// Loads the project's dependencies and adds their native types to the project; problems are reported as
    /// diagnostics of the project file.
    /// </summary>
    public static void LoadDependencies(FormatProject project, LibraryCatalog? catalog = null)
    {
        var loader = new LibraryLoader(project, catalog ?? LibraryCatalog.For(project));
        var where = project.FilePath != null ? new SourceLocation(project.Relative(project.FilePath)) : null;
        foreach (var dependency in project.Settings.Dependencies)
        {
            try
            {
                project.AddModule(loader.Load(dependency.Library, dependency.Version, []), dependency.AliasOrName);
            }
            catch (FormatDefinitionException e)
            {
                project.Diagnostics.Error(e.Message, e.Location ?? where);
            }
        }

        project.AddNatives(project.Modules.Values.SelectMany(m => m.Natives), where);
        project.AddGenerators(project.Modules.Values.SelectMany(m => m.Generators), where);
        project.AddCodegen(project.Modules.Values.SelectMany(m => m.CodegenExtensions));
    }

    private FormatModule Load(string name, string? version, IReadOnlyList<string> chain)
    {
        if (chain.Contains(name)) throw new FormatDefinitionException($"Libraries depend on each other: {string.Join(" -> ", chain.Append(name))}");
        if (_project.Modules.TryGetValue(name, out var loaded))
        {
            if (version != null && loaded.Version != version)
                throw new FormatDefinitionException($"Library {name} is needed in versions {loaded.Version} and {version}");
            return loaded;
        }

        var package = _catalog.Find(name, version) ?? throw new FormatDefinitionException(
            $"Library {name}{(version != null ? " " + version : "")} not found (searched {string.Join(", ", _catalog.Folders.Select(_project.Relative).Append("built-in libraries"))})");
        if (package.Settings.Kind != ProjectKind.Library) throw new FormatDefinitionException($"{package.Origin} is not a library (kind: library)");

        // The library as a project of its own, to check it and resolve its names as it sees them.
        var library = new FormatProject(package.Settings, _project.Directory) { Natives = _natives, Plugins = _project.Plugins };
        var dependencies = new List<FormatModule>();
        foreach (var dependency in package.Settings.Dependencies)
        {
            var module = Load(dependency.Library, dependency.Version, [.. chain, name]);
            dependencies.Add(module);
            library.AddModule(module, dependency.AliasOrName);
        }

        foreach (var module in _project.Modules.Values) library.AddModule(module);

        // Native types first: the definitions' references to them are told apart from type names by their names.
        var files = NativeFiles.Package(package);
        var natives = new DiagnosticBag();
        var generators = package.Files.Where(f => DeclaredGenerator.IsDeclaration(f.Text))
            .Select(f => DeclaredGenerator.Read(f.Text, f.Path, files, _project.Plugins, natives, $"{package.Origin}!/{f.Path}"))
            .OfType<DeclaredGenerator>()
            .ToList();
        var declared = package.Files.Where(f => DeclaredNative.IsDeclaration(f.Text) && !DeclaredGenerator.IsDeclaration(f.Text))
            .Select(f => DeclaredNative.Read(f.Text, f.Path, files, _project.Plugins, natives, $"{package.Origin}!/{f.Path}"))
            .OfType<DeclaredNative>()
            .ToList();
        var used = dependencies.SelectMany(d => d.Natives).Concat(declared).Distinct().ToList();
        library.AddNatives(used, new SourceLocation($"{package.Origin}"));
        var extensions = package.Files.Where(f => NativeCodegenExtension.IsDeclaration(f.Text))
            .Select(f => NativeCodegenExtension.Read(f.Text, f.Path, files, natives, $"{package.Origin}!/{f.Path}"))
            .OfType<NativeCodegenExtension>()
            .ToList();
        library.AddCodegen(extensions);
        // Only those without problems, which are reported once, here.
        extensions = extensions.Where(library.Natives.Extensions.Contains).ToList();
        foreach (var (path, text) in package.Files.Where(f => !FormatProject.IsDeclaration(f.Text)))
            if (DslReader.Read(text, $"{package.Origin}!/{path}", library.Diagnostics) is { } definition) library.Add(definition);
        Validation.Validator.Validate(library);
        foreach (var diagnostic in natives.Concat(library.Diagnostics.Where(d => d.Severity == Severity.Error)))
            _project.Diagnostics.Add(diagnostic with { Message = $"Library {package}: {diagnostic.Message}" });

        var definitions = library.Types.Values.ToList();
        new Baker(library, name).Bake(definitions);
        var result = new FormatModule(name, definitions) { Version = package.Version, Origin = package.Origin, Natives = used, Generators = generators,
            CodegenExtensions = extensions };
        _project.AddModule(result);
        return result;
    }

    /// <summary>Rewrites a library's definitions in place: qualified names, explicit defaults.</summary>
    private sealed class Baker(FormatProject library, string moduleName)
    {
        private readonly FormatDefaults _defaults = library.Defaults;

        private bool Inherits(string setting) => _defaults.Inherits(setting);

        public void Bake(IEnumerable<TypeDef> definitions)
        {
            foreach (var definition in definitions)
            {
                switch (definition)
                {
                    case StructDef structDef:
                        foreach (var field in structDef.Fields)
                        {
                            field.Type = Type(field.Type);
                            field.If = Expression(field.If);
                            field.Computed = Expression(field.Computed);
                        }

                        break;
                    case EnumDef enumDef:
                        enumDef.Type = Primitive(enumDef.Type);
                        break;
                    case VariantDef variant:
                        variant.Tag = variant.Tag == null ? null : Type(variant.Tag);
                        foreach (var variantCase in variant.Cases) variantCase.Type = variantCase.Type == null ? null : Type(variantCase.Type);
                        break;
                    case AliasDef alias:
                        alias.Target = Type(alias.Target);
                        break;
                    case BitfieldDef bitfield:
                        bitfield.Base = Primitive(bitfield.Base);
                        foreach (var member in bitfield.Members.Where(m => m.Type is not (null or "bool"))) member.Type = Name(member.Type!);
                        break;
                }
            }
        }

        /// <summary>A type name as the library resolves it, qualified so that it resolves the same in any project.</summary>
        private string Name(string reference) => library.Resolve(reference).Definition is { } target
            ? target.Owner == null ? $"{moduleName}.{target.Name}" : target.QualifiedName
            : reference;

        private string? Expression(string? text)
        {
            if (text == null || !Expr.TryParse(text, out var expression, out _)) return text;
            var renamed = expression!.RenameEnums(Name);
            return renamed == expression ? text : renamed.ToString();
        }

        private PrimitiveRef Primitive(PrimitiveRef primitive) =>
            primitive.Endian == null && Primitives.HasEndian(primitive.Kind) && !Inherits("endian")
                ? primitive with { Endian = _defaults.Endian }
                : primitive;

        private LengthSpec Length(LengthSpec length) => length.Kind switch
        {
            LengthKind.Default when !Inherits("length") => new LengthSpec(LengthKind.Prefix, Primitive(_defaults.Length)),
            LengthKind.Prefix => length with { Prefix = Primitive(length.Prefix!) },
            LengthKind.Expression or LengthKind.Until => length with { Expression = Expression(length.Expression) },
            _ => length,
        };

        /// <summary>
        /// The arguments the reference leaves out, from the library's defaults for the type; type arguments baked like
        /// any type (and written back as text).
        /// </summary>
        private NativeRef Native(NativeRef native)
        {
            if (!Inherits(native.Name) && _defaults.Natives.TryGetValue(native.Name, out var defaults))
            {
                var args = new List<string>();
                for (var i = 0; i < Math.Max(native.Args.Count, defaults.Count); i++)
                {
                    var given = i < native.Args.Count ? native.Args[i].Trim() : null;
                    args.Add(given is null or "" or "default" && i < defaults.Count ? defaults[i] : given!);
                }

                native = native with { Args = args };
            }

            var parsed = (NativeRef)library.Normalize(native);
            if (parsed.Types.All(t => t == null)) return native;
            var bakedArgs = parsed.Args.ToList();
            var bakedTypes = new TypeRef?[bakedArgs.Count];
            for (var i = 0; i < bakedArgs.Count; i++)
            {
                if (parsed.Types[i] is not { } type) continue;
                bakedTypes[i] = Type(type);
                bakedArgs[i] = DslWriter.FormatType(bakedTypes[i]!);
            }

            return new NativeRef(native.Name, bakedArgs) { Types = bakedTypes };
        }

        private TypeRef Type(TypeRef type) => type switch
        {
            PrimitiveRef primitive => Primitive(primitive),
            NamedRef named => new NamedRef(Name(named.Name), named.As == null ? null : Primitive(named.As), Expression(named.Tag)),
            StringRef str => str with { Encoding = str.Encoding ?? (Inherits("string") ? null : _defaults.String), Length = Length(str.Length) },
            BytesRef bytes => new BytesRef(Length(bytes.Length)),
            ArrayRef array => new ArrayRef(Type(array.Element), Length(array.Length)),
            MapRef map => new MapRef(Type(map.Key), Type(map.Value), Length(map.Length)),
            OptionalRef optional => new OptionalRef(Type(optional.Inner)),
            SizedRef sized => new SizedRef(Type(sized.Inner), Length(sized.Length)),
            FixedPointRef fixedPoint => fixedPoint with { Base = Primitive(fixedPoint.Base) },
            NativeRef native => Native(native),
            _ => type,
        };
    }
}
