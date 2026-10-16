using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>
/// The definitions of a library loaded as a dependency of a project. Its definitions belong to it
/// (<see cref="TypeDef.Owner"/>); a project references them as <c>Alias.Name</c>, or by name alone when the project has no
/// definition of that name and no other direct dependency has one.
/// </summary>
/// <remarks>
/// Definitions of a library must not depend on how the consuming project resolves names: references between them use
/// the qualified form <c>&lt;Name&gt;.&lt;Type&gt;</c>, which resolves to this module in every project that loads it.
/// </remarks>
public sealed class FormatModule
{
    private readonly Dictionary<string, TypeDef> _types = new(StringComparer.Ordinal);

    public FormatModule(string name, IEnumerable<TypeDef> definitions)
    {
        if (!Dsl.TypeExpressions.IsIdentifier(name)) throw new ArgumentException($"'{name}' is not a valid library name", nameof(name));
        Name = name;
        foreach (var definition in definitions)
        {
            definition.Owner = this;
            _types[definition.Name] = definition;
        }
    }

    /// <summary>The library's name: unique among the libraries a project loads, dependencies of dependencies included.</summary>
    public string Name { get; }
    public string? Version { get; init; }
    /// <summary>Where the library was loaded from, e.g. its package file; shown in the UI and in messages.</summary>
    public string? Origin { get; init; }
    public IReadOnlyDictionary<string, TypeDef> Types => _types;
    /// <summary>The native types the library's definitions use: its own and those of its dependencies.</summary>
    public IReadOnlyList<Natives.INativeType> Natives { get; init; } = [];
    /// <summary>The code generators the library declares (not those of its dependencies, which are modules of their own).</summary>
    public IReadOnlyList<Codegen.DeclaredGenerator> Generators { get; init; } = [];

    public TypeDef? Find(string name) => _types.GetValueOrDefault(name);

    public override string ToString() => Version == null ? Name : $"{Name} {Version}";
}

/// <summary>The definition a type name refers to, or why there is none.</summary>
public sealed record TypeLookup(TypeDef? Definition, string? Error)
{
    public static TypeLookup Found(TypeDef definition) => new(definition, null);

    public static TypeLookup Failed(string error) => new(null, error);
}
