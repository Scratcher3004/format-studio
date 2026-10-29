using System.Text.RegularExpressions;
using FormatStudio.Codegen;
using FormatStudio.Natives;

namespace FormatStudio.Projects;

/// <summary>
/// A library that the program Format Studio runs in provides in code, e.g. the <c>minecraft</c> library of
/// <c>FormatStudio.Minecraft</c>. A project uses it like a packaged one (<c>dependencies: [name]</c>); it is found after
/// the project's library folders, together with the packages embedded in Core.
/// </summary>
public sealed class HostLibrary
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    /// <summary>The native types the library provides to the projects that depend on it.</summary>
    public IReadOnlyList<INativeType> Natives { get; init; } = [];
    /// <summary>The code generators the library provides to the projects that depend on it, e.g. <c>netty</c>.</summary>
    public IReadOnlyList<ICodeGenerator> Generators { get; init; } = [];

    /// <summary>
    /// Names of native types that used to be built in. A project or library that uses one without depending on this
    /// library gets the dependency added (see <see cref="HostLibraries.WithLegacyDependencies"/>).
    /// </summary>
    public IReadOnlyList<string> LegacyNatives { get; init; } = [];
}

/// <summary>The libraries the host registered; the command line, the web UI and the tests register theirs at start-up.</summary>
public static class HostLibraries
{
    private static readonly Lock Gate = new();
    private static IReadOnlyList<HostLibrary> _libraries = [];

    public static IReadOnlyList<HostLibrary> All
    {
        get
        {
            lock (Gate) return _libraries;
        }
    }

    /// <summary>Registers a library, replacing the one with its name (registering the same library twice changes nothing).</summary>
    public static void Register(HostLibrary library)
    {
        lock (Gate) _libraries = [.. _libraries.Where(l => l.Name != library.Name), library];
    }

    /// <summary>The library that provides a native type, or null.</summary>
    public static HostLibrary? Of(INativeType type) => All.FirstOrDefault(l => l.Natives.Contains(type));

    /// <summary>The library that provides a code generator, or null.</summary>
    public static HostLibrary? Of(ICodeGenerator generator) => All.FirstOrDefault(l => l.Generators.Contains(generator));

    /// <summary>The libraries as packages, for the library catalog.</summary>
    internal static IEnumerable<LibraryPackage> Packages() => All.Select(library => new LibraryPackage
    {
        Settings = ProjectSettings.Parse($"name: {library.Name}\nkind: library\nversion: {library.Version}\n" +
            (library.Description.Length > 0 ? $"description: '{library.Description.Replace("'", "''")}'\n" : ""), $"built-in {library.Name}"),
        Files = [],
        Origin = $"built-in {library.Name}",
        HostNatives = library.Natives,
        HostGenerators = library.Generators,
    });

    /// <summary>
    /// The dependencies of a project or library, with the host libraries added whose former built-in native types
    /// (<see cref="HostLibrary.LegacyNatives"/>) it uses without declaring the dependency. It uses a type when a default
    /// is set for it or one of <paramref name="texts"/>, its definition files, contains its name as a word.
    /// </summary>
    // TODO(legacy-nbt): remove this once every project and library declares `dependencies: [minecraft]` (and the
    // HostLibrary.LegacyNatives, the call sites in LibraryLoader and the note in docs/libraries.md).
    internal static IReadOnlyList<LibraryDependency> WithLegacyDependencies(IReadOnlyList<LibraryDependency> declared,
        FormatDefaults defaults, IEnumerable<string> texts)
    {
        var result = declared;
        var textList = texts as IReadOnlyList<string> ?? [.. texts];
        foreach (var library in All)
        {
            if (library.LegacyNatives.Count == 0 || result.Any(d => d.Library == library.Name)) continue;
            var used = library.LegacyNatives.Any(name => defaults.Natives.ContainsKey(name)
                || textList.Any(text => Regex.IsMatch(text, $@"(?<![\w.\-]){Regex.Escape(name)}(?![\w\-])")));
            if (used) result = [.. result, new LibraryDependency { Library = library.Name }];
        }

        return result;
    }
}
