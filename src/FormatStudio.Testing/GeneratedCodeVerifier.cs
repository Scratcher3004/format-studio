using System.Reflection;
using System.Runtime.Loader;
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
        if (project.Generators.Get(target.Generator) is not CSharpGenerator)
            throw new ArgumentException($"{target.Generator} doesn't generate C#; use {nameof(CheckJavaAgainstSimulator)} for Java", nameof(target));
        return Check(project, target, new TargetTools(runtime, null), iterations);
    }

    /// <summary>
    /// Generates the Java of a <c>java-stream</c> target, compiles it with the JDK and checks every standalone type with
    /// <paramref name="iterations"/> random values in one Java process. The target must include the runtime (the default).
    /// </summary>
    public static IReadOnlyList<string> CheckJavaAgainstSimulator(FormatProject project, CodegenTarget target, JavaToolchain java, int iterations)
    {
        if (project.Generators.Get(target.Generator) is not JavaGenerator)
            throw new ArgumentException($"{target.Generator} doesn't generate Java", nameof(target));
        return Check(project, target, new TargetTools(null, java), iterations);
    }

    /// <summary>Encodes random values with the simulator, lets the generated code read and write them, and compares.</summary>
    private static IReadOnlyList<string> Check(FormatProject project, CodegenTarget target, TargetTools tools, int iterations)
    {
        var output = CodeGeneration.Generate(project, target);
        if (output.Diagnostics.HasErrors) throw new GeneratedCodeException(string.Join("\n", output.Diagnostics));
        // Types bound to existing ones (or skipped) have no generated code to check.
        var types = StandaloneTypes(project).Select(t => t.Name).Where(t => output.FileOf(t) != null).ToList();
        var codec = new Codec(project);
        var failures = new List<string>();
        var jobs = new List<HarnessJob>();
        foreach (var type in types)
        {
            for (var i = 0; i < iterations; i++)
            {
                var value = new RandomValues(project, Simulator.FuzzSeed(1, type, i)).Generate(new NamedRef(type));
                var encoded = codec.Encode(new NamedRef(type), value);
                if (encoded.Success) jobs.Add(HarnessJob.OfType(type, encoded.Bytes));
                else failures.Add($"{type}: the simulator can't encode a random value: {string.Join("; ", encoded.Diagnostics)}");
            }
        }

        using var harness = TargetHarness.Create(project, target, output, tools, types, []);
        var answers = harness.Run(jobs);
        for (var i = 0; i < jobs.Count; i++)
        {
            var (type, bytes) = (jobs[i].Type!, jobs[i].Bytes);
            if (answers[i].Error is { } error) failures.Add($"{type}: {error} for {Hex.Format(bytes)}");
            else if (!answers[i].Bytes.AsSpan().SequenceEqual(bytes)) failures.Add($"{type}: {Hex.Format(bytes)} became {Hex.Format(answers[i].Bytes!)}");
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
}

/// <summary>The generated code doesn't compile, or the generator reported errors.</summary>
public sealed class GeneratedCodeException(string message) : Exception(message);
