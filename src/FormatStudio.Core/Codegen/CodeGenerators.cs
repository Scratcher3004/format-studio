using FormatStudio.Codegen.CSharp;
using FormatStudio.Codegen.Java;
using FormatStudio.Model;

namespace FormatStudio.Codegen;

/// <summary>The code generators a project can use, by name, in order. Immutable; <see cref="With"/> adds some.</summary>
public sealed class CodeGenerators
{
    private readonly List<ICodeGenerator> _generators;

    private CodeGenerators(List<ICodeGenerator> generators) => _generators = generators;

    /// <summary>The generators that come with Format Studio.</summary>
    public static CodeGenerators BuiltIn { get; } = new(
    [
        new CSharpGenerator(CSharpIo.Span),
        new CSharpGenerator(CSharpIo.Stream),
        new JavaGenerator(),
    ]);

    public static bool IsBuiltIn(string name) => BuiltIn.Find(name) != null;

    public IReadOnlyList<ICodeGenerator> All => _generators;

    public ICodeGenerator? Find(string name) => _generators.FirstOrDefault(g => g.Name == name);

    /// <summary>The generator with a name; throws when there is none.</summary>
    public ICodeGenerator Get(string name) =>
        Find(name) ?? throw new FormatDefinitionException($"Unknown generator '{name}' ({string.Join(", ", _generators.Select(g => g.Name))})");

    /// <summary>
    /// A copy with <paramref name="generators"/> added. A generator named like another one (except the same one again)
    /// is left out and reported to <paramref name="conflict"/>.
    /// </summary>
    public CodeGenerators With(IEnumerable<ICodeGenerator> generators, Action<ICodeGenerator, ICodeGenerator> conflict)
    {
        var result = new List<ICodeGenerator>(_generators);
        foreach (var generator in generators)
        {
            if (result.FirstOrDefault(g => g.Name == generator.Name) is { } existing)
            {
                if (existing != generator) conflict(existing, generator);
                continue;
            }

            result.Add(generator);
        }

        return new CodeGenerators(result);
    }
}
