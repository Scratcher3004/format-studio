using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Model;

namespace FormatStudio.Natives;

/// <summary>
/// Code of a native type for generators it has no code for itself, declared in a project or library file
/// (<c>native-codegen: rgb</c>): e.g. Java for a type that ships only C#, or code for a plugin generator. The type can
/// come from anywhere (built-in, the project, a library); one type has at most one source of code per generator.
/// </summary>
public sealed partial class NativeCodegenExtension
{
    private static readonly HashSet<string> Keys = ["native-codegen", "doc", "codegen"];

    private NativeCodegenExtension(string type, IReadOnlyDictionary<string, NativeCodegen> codegen)
    {
        Type = type;
        Codegen = codegen;
    }

    /// <summary>The native type the code is for.</summary>
    public string Type { get; }
    /// <summary>The code, by generator.</summary>
    public IReadOnlyDictionary<string, NativeCodegen> Codegen { get; }
    /// <summary>The declaration file, relative to the project or package.</summary>
    public required SourceLocation Source { get; init; }
    /// <summary>The runtime files of the code, to be packed with the declaration.</summary>
    public IReadOnlyList<string> Files { get; private init; } = [];

    [GeneratedRegex(@"^native-codegen\s*:", RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    /// <summary>Whether a source file declares code for a native type (its top-level mapping has <c>native-codegen</c>).</summary>
    public static bool IsDeclaration(string text) => DeclarationRegex().IsMatch(text);

    /// <summary>
    /// Reads a declaration; problems are added to <paramref name="diagnostics"/>. Returns null when it is unusable.
    /// Whether the type exists and has no code of its own for the generators is checked when it is added to
    /// <see cref="NativeTypes"/>.
    /// </summary>
    /// <param name="file">The declaration's path in <paramref name="files"/>; file patterns are relative to its folder.</param>
    /// <param name="generators">The generators the code is checked against; null for no check.</param>
    public static NativeCodegenExtension? Read(string text, string file, NativeFiles files, DiagnosticBag diagnostics, string? displayFile = null,
        CodeGenerators? generators = null)
    {
        var where = new SourceLocation(displayFile ?? file);
        try
        {
            if (YamlValues.ParseYaml(text) is not JsonObject map) throw new FormatDefinitionException("A native-codegen declaration must be a mapping");
            foreach (var key in map.Select(p => p.Key).Where(k => !Keys.Contains(k)))
                diagnostics.Warning($"Unknown key '{key}' in a native-codegen declaration", where);
            var type = map["native-codegen"]?.ToString() ?? "";
            if (!TypeExpressions.IsIdentifier(type)) throw new FormatDefinitionException($"'{type}' is not a valid native type name");
            if (map["codegen"] is not JsonObject generatorMap || generatorMap.Count == 0)
                throw new FormatDefinitionException($"native-codegen for {type} needs 'codegen', mapping generator names to their code");

            var used = new List<string>();
            var codegen = new Dictionary<string, NativeCodegen>(StringComparer.Ordinal);
            foreach (var (generator, node) in generatorMap)
                codegen[generator] = DeclaredNative.ReadCodegen(type, generator, node, file, files, used, generators, diagnostics, where);
            return new NativeCodegenExtension(type, codegen) { Source = where, Files = used.Distinct().ToList() };
        }
        catch (Exception e) when (e is FormatDefinitionException or YamlDotNet.Core.YamlException or InvalidOperationException or System.FormatException)
        {
            diagnostics.Error(e.Message, where);
            return null;
        }
    }

    public override string ToString() => $"{Type}: {string.Join(", ", Codegen.Keys)} ({Source.File})";
}
