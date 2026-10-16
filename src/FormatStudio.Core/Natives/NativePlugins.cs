using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using FormatStudio.Model;

namespace FormatStudio.Natives;

/// <summary>
/// Which plugin assemblies may be loaded: plugins run arbitrary code, so a server that opens other people's projects
/// allows none, or only those it knows by their SHA-256.
/// </summary>
public sealed class PluginPolicy
{
    private readonly HashSet<string>? _allowed;

    private PluginPolicy(HashSet<string>? allowed, string description)
    {
        _allowed = allowed;
        Description = description;
    }

    /// <summary>Every plugin is loaded (the command line and the local web UI).</summary>
    public static PluginPolicy All { get; } = new(null, "all");

    /// <summary>No plugin is loaded.</summary>
    public static PluginPolicy None { get; } = new([], "none");

    /// <summary>Only the assemblies with these SHA-256 hashes (hex) are loaded.</summary>
    public static PluginPolicy Only(IEnumerable<string> hashes) =>
        new(hashes.Select(h => h.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), "allow-list");

    /// <summary>
    /// Reads a policy as given on the command line or in <c>FORMATSTUDIO_PLUGINS</c>: <c>all</c>, <c>none</c> or
    /// SHA-256 hashes separated by commas; null or empty for <paramref name="fallback"/>.
    /// </summary>
    public static PluginPolicy Parse(string? text, PluginPolicy? fallback = null) => text?.Trim() switch
    {
        null or "" => fallback ?? All,
        "all" => All,
        "none" => None,
        var hashes => Only(hashes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(hash => hash.Length == 64 && Convert.FromHexString(hash) is not null
                ? hash
                : throw new FormatDefinitionException($"'{hash}' is not a SHA-256 hash (plugins: all, none or hashes separated by commas)"))),
    };

    /// <summary>all, none or allow-list.</summary>
    public string Description { get; }

    /// <summary>The hex SHA-256 of an assembly, as an allow-list names it.</summary>
    public static string Hash(byte[] assembly) => Convert.ToHexStringLower(SHA256.HashData(assembly));

    /// <summary>Why an assembly may not be loaded, or null when it may.</summary>
    public string? Refuse(byte[] assembly) => _allowed switch
    {
        null => null,
        { Count: 0 } => "plugins are disabled",
        _ when _allowed.Contains(Hash(assembly)) => null,
        _ => $"the plugin isn't allowed (SHA-256 {Hash(assembly)})",
    };
}

/// <summary>
/// Loads the assemblies of plugins (native types, code generators). Each plugin gets a collectible load context of its own, which shares
/// Format Studio and the framework with the host and loads other dependencies from the plugin's folder. Assemblies are
/// read into memory, so the files aren't locked and can be rebuilt; a plugin whose bytes are unchanged is loaded once.
/// </summary>
public static class NativePlugins
{
    private static readonly Lock Lock = new();
    private static readonly Dictionary<string, (string Hash, PluginContext Context, Assembly Assembly)> Loaded = new(StringComparer.Ordinal);

    private static readonly Lazy<HashSet<string>> PlatformAssemblies = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(Path.GetFileNameWithoutExtension)
        .OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether an assembly comes from the host (Format Studio, the framework) instead of the plugin's folder.</summary>
    public static bool IsShared(string name) =>
        PlatformAssemblies.Value.Contains(name)
        || AssemblyLoadContext.Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads a plugin assembly; throws <see cref="FormatDefinitionException"/> when it can't or may not.</summary>
    public static Assembly Load(NativeFiles files, string path, PluginPolicy policy)
    {
        var bytes = files.Read(path) ?? throw new FormatDefinitionException($"{files.Describe(path)} doesn't exist");
        if (policy.Refuse(bytes) is { } reason) throw new PluginRefusedException($"{path} isn't loaded: {reason}");
        var key = files.Describe(path);
        var hash = PluginPolicy.Hash(bytes);
        lock (Lock)
        {
            if (Loaded.TryGetValue(key, out var loaded))
            {
                if (loaded.Hash == hash) return loaded.Assembly;
                // A rebuilt plugin: the old context goes away once nothing uses its types any more.
                loaded.Context.Unload();
                Loaded.Remove(key);
            }

            var context = new PluginContext(key, files, path, policy);
            try
            {
                var symbols = files.Read(Path.ChangeExtension(path, ".pdb"));
                var assembly = context.LoadFromStream(new MemoryStream(bytes), symbols == null ? null : new MemoryStream(symbols));
                Loaded[key] = (hash, context, assembly);
                return assembly;
            }
            catch (BadImageFormatException e)
            {
                context.Unload();
                throw new FormatDefinitionException($"{path} is not a .NET assembly: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Loads a plugin and creates an instance of its class <paramref name="className"/>, which must implement
    /// <typeparamref name="T"/> and have a public parameterless constructor. Throws <see cref="PluginRefusedException"/>
    /// when the policy doesn't allow the plugin and <see cref="FormatDefinitionException"/> for other problems; the
    /// constructor's exceptions are passed on.
    /// </summary>
    /// <param name="owner">What the class implements, for messages: the native type's or generator's name.</param>
    public static T Create<T>(NativeFiles files, string path, string className, PluginPolicy policy, string owner) where T : class
    {
        var assembly = Load(files, path, policy);
        var type = assembly.GetType(className, throwOnError: false)
            ?? throw new FormatDefinitionException($"{owner}: {path} has no class {className}");
        if (!typeof(T).IsAssignableFrom(type))
        {
            throw new FormatDefinitionException(type.GetInterfaces().Any(i => i.FullName == typeof(T).FullName)
                ? $"{owner}: {className} implements another FormatStudio.Core's {typeof(T).Name}; the plugin must not ship its own FormatStudio.Core.dll"
                : $"{owner}: {className} doesn't implement {typeof(T).FullName}");
        }

        return (T)(Activator.CreateInstance(type) ?? throw new FormatDefinitionException($"{owner}: {className} couldn't be created"));
    }

    /// <summary>
    /// The assemblies a plugin needs from its folder (itself first, then the dependencies it references there,
    /// transitively, with their <c>.pdb</c> files); used to pack a plugin into a library.
    /// </summary>
    public static IReadOnlyList<string> Files(NativeFiles files, string path)
    {
        var result = new List<string>();
        var pending = new Queue<string>([path]);
        while (pending.TryDequeue(out var next))
        {
            if (result.Contains(next) || files.Read(next) is not { } bytes) continue;
            result.Add(next);
            if (Path.ChangeExtension(next, ".pdb") is var symbols && files.Read(symbols) != null) result.Add(symbols);
            foreach (var reference in References(bytes).Where(r => !IsShared(r)))
                pending.Enqueue(NativeFiles.Join(path, reference + ".dll"));
        }

        return result;
    }

    private static IEnumerable<string> References(byte[] assembly)
    {
        try
        {
            using var reader = new PEReader(new MemoryStream(assembly));
            var metadata = reader.GetMetadataReader();
            return metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToList();
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    private sealed class PluginContext(string name, NativeFiles files, string path, PluginPolicy policy) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } dependency || IsShared(dependency)) return null;
            var file = NativeFiles.Join(path, dependency + ".dll");
            if (files.Read(file) is not { } bytes) return null;
            if (policy.Refuse(bytes) is { } reason) throw new FileLoadException($"{file} isn't loaded: {reason}");
            var symbols = files.Read(Path.ChangeExtension(file, ".pdb"));
            return LoadFromStream(new MemoryStream(bytes), symbols == null ? null : new MemoryStream(symbols));
        }
    }
}

/// <summary>A plugin that the <see cref="PluginPolicy"/> doesn't allow.</summary>
public sealed class PluginRefusedException(string message) : FormatDefinitionException(message);
