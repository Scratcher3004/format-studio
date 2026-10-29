using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Natives;

namespace FormatStudio.Codegen;

/// <summary>
/// A definition bound to an existing type of the target language, declared in a project or library file
/// (<c>bind: Vec3</c>): per generator, the type its values have and how they are read and written (or their codec),
/// like the code of a native type. The definition is not generated for those generators; its references use the
/// binding. The definition can be the project's own or a library's; a library's binding applies wherever the library
/// is loaded.
/// </summary>
public sealed partial class TypeBinding
{
    private static readonly HashSet<string> Keys = ["bind", "doc", "codegen"];

    private TypeBinding(string definition, IReadOnlyDictionary<string, NativeCodegen> codegen)
    {
        Definition = definition;
        Codegen = codegen;
    }

    /// <summary>The bound definition as written; in a library's binding, its qualified name.</summary>
    public string Definition { get; private init; }
    /// <summary>The code, by generator.</summary>
    public IReadOnlyDictionary<string, NativeCodegen> Codegen { get; }
    public string? Doc { get; private init; }
    /// <summary>The declaration file, relative to the project or package.</summary>
    public required SourceLocation Source { get; init; }
    /// <summary>The runtime files of the code, to be packed with the declaration.</summary>
    public IReadOnlyList<string> Files { get; private init; } = [];

    [GeneratedRegex(@"^bind\s*:", RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    /// <summary>Whether a source file binds a definition (its top-level mapping has <c>bind</c>).</summary>
    public static bool IsDeclaration(string text) => DeclarationRegex().IsMatch(text);

    /// <summary>
    /// Reads a declaration; problems are added to <paramref name="diagnostics"/>. Returns null when it is unusable.
    /// Whether the definition exists is checked by the validator.
    /// </summary>
    /// <param name="file">The declaration's path in <paramref name="files"/>; file patterns are relative to its folder.</param>
    /// <param name="generators">The generators the code is checked against; null for no check.</param>
    public static TypeBinding? Read(string text, string file, NativeFiles files, DiagnosticBag diagnostics, string? displayFile = null,
        CodeGenerators? generators = null)
    {
        var where = new SourceLocation(displayFile ?? file);
        try
        {
            if (YamlValues.ParseYaml(text) is not JsonObject map) throw new FormatDefinitionException("A binding must be a mapping");
            foreach (var key in map.Select(p => p.Key).Where(k => !Keys.Contains(k)))
                diagnostics.Warning($"Unknown key '{key}' in a binding", where);
            var definition = map["bind"]?.ToString() ?? "";
            if (!TypeExpressions.IsTypeName(definition)) throw new FormatDefinitionException($"'{definition}' is not a valid definition name");
            if (map["codegen"] is not JsonObject generatorMap || generatorMap.Count == 0)
                throw new FormatDefinitionException($"The binding of {definition} needs 'codegen', mapping generator names to their code");

            var used = new List<string>();
            var codegen = new Dictionary<string, NativeCodegen>(StringComparer.Ordinal);
            foreach (var (generator, node) in generatorMap)
                codegen[generator] = DeclaredNative.ReadCodegen(definition, generator, node, file, files, used, generators, diagnostics, where, binding: true);
            return new TypeBinding(definition, codegen) { Source = where, Doc = map["doc"]?.ToString(), Files = used.Distinct().ToList() };
        }
        catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException or InvalidOperationException or System.FormatException)
        {
            diagnostics.Error(e.Message, where);
            return null;
        }
    }

    /// <summary>The same binding for another name of its definition (a library's, qualified for the projects that load it).</summary>
    public TypeBinding WithDefinition(string definition) => new(definition, Codegen) { Source = Source, Doc = Doc, Files = Files };

    public override string ToString() => $"{Definition}: {string.Join(", ", Codegen.Keys)} ({Source.File})";
}
