using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FormatStudio.Codegen;
using FormatStudio.Codegen.CSharp;
using FormatStudio.Codegen.Java;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FormatStudio.Testing;

/// <summary>
/// Checks generated code against the simulator: compiles the output of a codegen target, then reads and writes back
/// bytes that the simulator encoded from random values. Every type must come back byte for byte. C# (spans and streams)
/// is compiled with Roslyn against its runtime; Java with a JDK (see <see cref="JavaToolchain"/>). Independent of the
/// test framework: problems are returned or thrown as <see cref="GeneratedCodeException"/>.
/// </summary>
public static class GeneratedCodeVerifier
{
    /// <summary>Types whose <c>Read</c> works on its own (variants with an external tag need the tag).</summary>
    public static IEnumerable<TypeDef> StandaloneTypes(FormatProject project) => project.Types.Values
        .Where(d => d is StructDef or BitfieldDef || d is VariantDef { Tag: not null })
        .OrderBy(d => d.Name, StringComparer.Ordinal);

    /// <summary>
    /// Generates the C# of a <c>csharp</c> or <c>csharp-stream</c> target and checks every standalone type with
    /// <paramref name="iterations"/> random values. Returns one message per failure (empty when everything matches).
    /// </summary>
    /// <param name="runtime">The assembly with the runtime the target's options name (reader, writer, interfaces).</param>
    public static IReadOnlyList<string> CheckAgainstSimulator(FormatProject project, CodegenTarget target, Assembly runtime, int iterations)
    {
        if (project.Generators.Get(target.Generator) is not CSharpGenerator generator)
            throw new ArgumentException($"{target.Generator} doesn't generate C#; use {nameof(CheckJavaAgainstSimulator)} for Java", nameof(target));
        var output = Generate(project, target);
        var options = CSharpOptions.From(target.Options, generator.Io);
        var types = StandaloneTypes(project).ToList();
        var assembly = Compile(output, CSharpHarness(types, options), runtime);
        var harness = (Dictionary<string, Func<byte[], byte[]>>)assembly.GetType("Harness")!.GetField("All")!.GetValue(null)!;
        return Check(project, types, iterations, cases => cases.Select(c =>
        {
            try
            {
                return new RoundTrip(harness[c.Type](c.Bytes), null);
            }
            catch (Exception e)
            {
                return new RoundTrip(null, $"{e.GetType().Name}: {e.Message}");
            }
        }).ToList());
    }

    /// <summary>
    /// Generates the Java of a <c>java-stream</c> target, compiles it with the JDK and checks every standalone type with
    /// <paramref name="iterations"/> random values in one Java process. The target must include the runtime (the default).
    /// </summary>
    public static IReadOnlyList<string> CheckJavaAgainstSimulator(FormatProject project, CodegenTarget target, JavaToolchain java, int iterations)
    {
        if (project.Generators.Get(target.Generator) is not JavaGenerator)
            throw new ArgumentException($"{target.Generator} doesn't generate Java", nameof(target));
        var output = Generate(project, target);
        var options = JavaOptions.From(target.Options);
        var types = StandaloneTypes(project).ToList();
        var directory = Path.Combine(Path.GetTempPath(), "format-studio-java-" + Guid.NewGuid().ToString("N"));
        try
        {
            var classes = java.Compile(output, JavaHarness(types, options), directory);
            return Check(project, types, iterations, cases =>
            {
                var input = string.Join("\n", cases.Select(c => $"{c.Type} {Convert.ToHexString(c.Bytes)}")) + "\n";
                var lines = java.Run(classes, "Harness", input).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length != cases.Count) throw new GeneratedCodeException($"The harness answered {lines.Length} of {cases.Count} cases");
                return lines.Select(line => line.StartsWith('!') ? new RoundTrip(null, line[1..]) : new RoundTrip(Convert.FromHexString(line.Trim()), null)).ToList();
            });
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static GeneratedOutput Generate(FormatProject project, CodegenTarget target)
    {
        var output = CodeGeneration.Generate(project, target);
        if (output.Diagnostics.HasErrors) throw new GeneratedCodeException(string.Join("\n", output.Diagnostics));
        return output;
    }

    private readonly record struct RoundTrip(byte[]? Bytes, string? Error);

    private readonly record struct Case(string Type, byte[] Bytes);

    /// <summary>Encodes random values with the simulator, lets the generated code read and write them, and compares.</summary>
    private static IReadOnlyList<string> Check(FormatProject project, IReadOnlyList<TypeDef> types, int iterations, Func<IReadOnlyList<Case>, IReadOnlyList<RoundTrip>> run)
    {
        var codec = new Codec(project);
        var failures = new List<string>();
        var cases = new List<Case>();
        foreach (var type in types)
        {
            for (var i = 0; i < iterations; i++)
            {
                var value = new RandomValues(project, HashCode.Combine(type.Name, i)).Generate(new NamedRef(type.Name));
                var encoded = codec.Encode(new NamedRef(type.Name), value);
                if (encoded.Success) cases.Add(new Case(type.Name, encoded.Bytes));
                else failures.Add($"{type.Name}: the simulator can't encode a random value: {string.Join("; ", encoded.Diagnostics)}");
            }
        }

        var results = run(cases);
        for (var i = 0; i < cases.Count; i++)
        {
            var (type, bytes) = cases[i];
            if (results[i].Error is { } error) failures.Add($"{type}: {error} for {Hex.Format(bytes)}");
            else if (!results[i].Bytes.AsSpan().SequenceEqual(bytes)) failures.Add($"{type}: {Hex.Format(bytes)} became {Hex.Format(results[i].Bytes!)}");
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
    private static string CSharpHarness(IReadOnlyList<TypeDef> types, CSharpOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"using {options.RuntimeNamespace};");
        builder.AppendLine("public static class Harness {");
        builder.AppendLine("  public static readonly global::System.Collections.Generic.Dictionary<string, global::System.Func<byte[], byte[]>> All = new() {");
        foreach (var type in types) builder.AppendLine($"    [\"{type.Name}\"] = {type.Name},");
        builder.AppendLine("  };");
        foreach (var type in types)
        {
            if (options.Io == CSharpIo.Stream)
            {
                // A length of -1 reads like a network stream: counts and the end are only known from the data.
                builder.AppendLine($$"""
                      static byte[] {{type.Name}}(byte[] data) {
                        var reader = new {{options.ReaderType}}(new global::System.IO.MemoryStream(data), -1);
                        var value = global::{{options.Namespace}}.{{type.Name}}.Read(reader);
                        if (!reader.AtEnd) throw new global::System.Exception("bytes left");
                        var buffer = new global::System.IO.MemoryStream();
                        value.Write(new {{options.WriterType}}(buffer));
                        return buffer.ToArray();
                      }
                    """);
                continue;
            }

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

    /// <summary>
    /// <c>Harness.main</c> reads lines <c>Type HEX</c> from stdin and prints the hex of the value written back, or
    /// <c>!error</c>.
    /// </summary>
    private static string JavaHarness(IReadOnlyList<TypeDef> types, JavaOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"import {options.RuntimePackage}.*;");
        builder.AppendLine("""
            import java.io.*;
            import java.nio.charset.StandardCharsets;

            public final class Harness {
                public static void main(String[] args) throws Exception {
                    BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
                    PrintStream out = new PrintStream(new FileOutputStream(FileDescriptor.out), false, "UTF-8");
                    String line;
                    while ((line = in.readLine()) != null) {
                        if (line.isEmpty()) continue;
                        int space = line.indexOf(' ');
                        try {
                            out.println(toHex(roundTrip(line.substring(0, space), fromHex(line.substring(space + 1)))));
                        } catch (Exception e) {
                            out.println("!" + e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()).replace('\n', ' '));
                        }
                    }
                    out.flush();
                }

                static byte[] fromHex(String hex) {
                    byte[] bytes = new byte[hex.length() / 2];
                    for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
                    return bytes;
                }

                static String toHex(byte[] bytes) {
                    StringBuilder builder = new StringBuilder();
                    for (byte b : bytes) builder.append(String.format("%02X", b & 0xFF));
                    return builder.toString();
                }

                static byte[] roundTrip(String type, byte[] data) throws IOException {
                    // No length: reads like a network stream, counts and the end are only known from the data.
                    FormatReader reader = new FormatReader(new ByteArrayInputStream(data));
                    FormatSerializable value;
                    switch (type) {
            """);
        foreach (var type in types)
            builder.AppendLine($"            case \"{type.Name}\": value = {options.Package}.{type.Name}.read(reader); break;");
        builder.AppendLine("""
                        default: throw new IllegalArgumentException(type);
                    }
                    if (!reader.atEnd()) throw new IllegalStateException("bytes left");
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    value.write(new FormatWriter(buffer));
                    return buffer.toByteArray();
                }
            }
            """);
        return builder.ToString();
    }
}

/// <summary>The generated code doesn't compile, or the generator reported errors.</summary>
public sealed class GeneratedCodeException(string message) : Exception(message);
