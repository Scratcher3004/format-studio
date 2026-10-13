using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FormatStudio.Codegen;
using FormatStudio.Codegen.CSharp;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FormatStudio.Testing;

/// <summary>
/// Checks generated C# against the simulator: compiles the output of a <c>csharp</c> codegen target with Roslyn
/// against its runtime, then reads and writes back bytes that the simulator encoded from random values. Every type must
/// come back byte for byte. Independent of the test framework: problems are returned or thrown as
/// <see cref="GeneratedCodeException"/>.
/// </summary>
public static class GeneratedCodeVerifier
{
    /// <summary>Types whose <c>Read</c> works on its own (variants with an external tag need the tag).</summary>
    public static IEnumerable<TypeDef> StandaloneTypes(FormatProject project) => project.Types.Values
        .Where(d => d is StructDef or BitfieldDef || d is VariantDef { Tag: not null })
        .OrderBy(d => d.Name, StringComparer.Ordinal);

    /// <summary>
    /// Generates the target's code and checks every standalone type with <paramref name="iterations"/> random values.
    /// Returns one message per failure (empty when everything matches).
    /// </summary>
    /// <param name="runtime">The assembly with the runtime the target's options name (reader, writer, interfaces).</param>
    public static IReadOnlyList<string> CheckAgainstSimulator(FormatProject project, CodegenTarget target, Assembly runtime, int iterations)
    {
        var output = CodeGeneration.Generate(project, target);
        if (output.Diagnostics.HasErrors) throw new GeneratedCodeException(string.Join("\n", output.Diagnostics));
        var options = CSharpOptions.From(target.Options);
        var types = StandaloneTypes(project).ToList();
        var assembly = Compile(output, Harness(types, options), runtime);
        var harness = (Dictionary<string, Func<byte[], byte[]>>)assembly.GetType("Harness")!.GetField("All")!.GetValue(null)!;

        var codec = new Codec(project);
        var failures = new List<string>();
        foreach (var type in types)
        {
            for (var i = 0; i < iterations; i++)
            {
                var value = new RandomValues(project, HashCode.Combine(type.Name, i)).Generate(new NamedRef(type.Name));
                var encoded = codec.Encode(new NamedRef(type.Name), value);
                if (!encoded.Success)
                {
                    failures.Add($"{type.Name}: the simulator can't encode a random value: {string.Join("; ", encoded.Diagnostics)}");
                    continue;
                }

                try
                {
                    var rewritten = harness[type.Name](encoded.Bytes);
                    if (!rewritten.AsSpan().SequenceEqual(encoded.Bytes))
                        failures.Add($"{type.Name}: {Hex.Format(encoded.Bytes)} became {Hex.Format(rewritten)}");
                }
                catch (Exception e)
                {
                    failures.Add($"{type.Name}: {e.GetType().Name}: {e.Message} for {Hex.Format(encoded.Bytes)}");
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// Compiles generated code plus extra sources (nullable enabled, warnings count as errors) and loads it into a
    /// collectible context.
    /// </summary>
    public static Assembly Compile(GeneratedOutput output, string extraSource, Assembly runtime)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var trees = output.Files.Select(f => CSharpSyntaxTree.ParseText(f.Content, parseOptions, f.Path))
            .Append(CSharpSyntaxTree.ParseText(extraSource, parseOptions, "Extra.cs"));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(runtime.Location));
        var compilation = CSharpCompilation.Create("Generated" + Guid.NewGuid().ToString("N"), trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        var problems = emit.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => d.ToString()).ToList();
        if (!emit.Success || problems.Count > 0) throw new GeneratedCodeException(string.Join("\n", problems.Take(30)));
        stream.Position = 0;
        return new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(stream);
    }

    /// <summary>A static class <c>Harness</c> with <c>All</c>: type name → read the bytes, write the value back.</summary>
    private static string Harness(IReadOnlyList<TypeDef> types, CSharpOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"using {options.RuntimeNamespace};");
        builder.AppendLine("public static class Harness {");
        builder.AppendLine("  public static readonly global::System.Collections.Generic.Dictionary<string, global::System.Func<byte[], byte[]>> All = new() {");
        foreach (var type in types) builder.AppendLine($"    [\"{type.Name}\"] = {type.Name},");
        builder.AppendLine("  };");
        foreach (var type in types)
        {
            builder.AppendLine($$"""
                  static byte[] {{type.Name}}(byte[] data) {
                    var reader = new {{options.ReaderType}}(data);
                    var value = global::{{options.Namespace}}.{{type.Name}}.Read(ref reader);
                    if (reader.Remaining != 0) throw new global::System.Exception($"{reader.Remaining} bytes left");
                    var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
                    var writer = new {{options.WriterType}}(buffer);
                    value.Write(ref writer);
                    return buffer.WrittenSpan.ToArray();
                  }
                """);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }
}

/// <summary>The generated code doesn't compile, or the generator reported errors.</summary>
public sealed class GeneratedCodeException(string message) : Exception(message);
