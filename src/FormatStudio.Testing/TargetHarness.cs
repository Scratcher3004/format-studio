using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using FormatStudio.Codegen;
using FormatStudio.Codegen.CSharp;
using FormatStudio.Codegen.Java;
using FormatStudio.Projects;

namespace FormatStudio.Testing;

/// <summary>
/// Bytes for the generated code to read and write back: as a type (<see cref="Type"/>), or as the packet a registry
/// reads for an id (<see cref="Registry"/>: a protocol's name, or "" for the registry of a project without protocols).
/// </summary>
internal sealed record HarnessJob(string? Type, string? Registry, ulong Id, byte[] Bytes)
{
    public static HarnessJob OfType(string type, byte[] bytes) => new(type, null, 0, bytes);
    public static HarnessJob OfPacket(string registry, ulong id, byte[] bytes) => new(null, registry, id, bytes);
}

/// <summary>
/// What the generated code made of a job: the bytes it wrote back, or the error it threw. A registry job also has the
/// name of the packet the registry read; <see cref="Unknown"/> when the registry has no packet with the id.
/// </summary>
internal sealed record HarnessAnswer(byte[]? Bytes, string? Error, string? Packet, bool Unknown, TimeSpan Duration)
{
    public bool Success => Error == null && !Unknown;
}

/// <summary>
/// The generated code of a target, compiled with a small program that runs <see cref="HarnessJob"/>s: C# in this
/// process (Roslyn), Java in one JVM per batch. Only the types and registries the jobs need are part of it.
/// </summary>
internal abstract class TargetHarness : IDisposable
{
    public abstract IReadOnlyList<HarnessAnswer> Run(IReadOnlyList<HarnessJob> jobs);

    public virtual void Dispose()
    {
    }

    /// <summary>Compiles the target's generated code with a harness for <paramref name="types"/> and <paramref name="registries"/>.</summary>
    public static TargetHarness Create(FormatProject project, CodegenTarget target, GeneratedOutput output, TargetTools tools,
        IReadOnlyCollection<string> types, IReadOnlyCollection<string> registries)
    {
        return project.Generators.Get(target.Generator) switch
        {
            CSharpGenerator csharp => new CSharpHarness(output, CSharpOptions.From(target.Options, csharp.Io),
                tools.CSharpRuntime ?? throw new GeneratedCodeException("No C# runtime to compile the generated code against"), types, registries,
                type => project.Find(type) is { } definition ? CSharpNames.TypeName(project, definition) : type),
            JavaGenerator => new JavaHarness(output, JavaOptions.From(target.Options),
                tools.Java ?? throw new GeneratedCodeException(JavaToolchain.Missing), types, registries,
                type => project.Find(type) is { } definition ? JavaNames.TypeName(project, definition) : type),
            _ => throw new GeneratedCodeException($"Tests run against C# and Java targets, not {target.Generator}"),
        };
    }

    /// <summary>A registry's class below the target's namespace or package: <c>Packets</c> or <c>Packets.Protocol</c>.</summary>
    protected static string RegistryClass(string registryOption, string registry) => registry.Length == 0 ? registryOption : $"{registryOption}.{registry}";

    /// <summary>The harness's method for a type; prefixed, so that a type can't be named like the harness's own members.</summary>
    protected static string TypeMethod(string type) => "T_" + type;

    /// <summary>The harness's method for a registry.</summary>
    protected static string RegistryMethod(string registry) => "R_" + registry;

    private sealed class CSharpHarness : TargetHarness
    {
        private readonly Dictionary<string, Func<byte[], byte[]>> _types;
        private readonly Dictionary<string, Func<uint, byte[], (string Name, byte[] Bytes)?>> _registries;

        /// <param name="className">The generated class of a type name.</param>
        public CSharpHarness(GeneratedOutput output, CSharpOptions options, Assembly runtime, IReadOnlyCollection<string> types, IReadOnlyCollection<string> registries,
            Func<string, string> className)
        {
            var assembly = GeneratedCodeVerifier.Compile(output, Source(options, types, registries, className), runtime);
            var harness = assembly.GetType("Harness")!;
            _types = (Dictionary<string, Func<byte[], byte[]>>)harness.GetField("Types")!.GetValue(null)!;
            _registries = (Dictionary<string, Func<uint, byte[], (string, byte[])?>>)harness.GetField("Registries")!.GetValue(null)!;
        }

        public override IReadOnlyList<HarnessAnswer> Run(IReadOnlyList<HarnessJob> jobs) => jobs.Select(Run).ToList();

        private HarnessAnswer Run(HarnessJob job)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (job.Type != null) return new HarnessAnswer(_types[job.Type](job.Bytes), null, null, false, watch.Elapsed);
                // The registries take a uint id; a larger one is no packet's.
                var packet = job.Id <= uint.MaxValue ? _registries[job.Registry!]((uint)job.Id, job.Bytes) : null;
                return packet is { } read
                    ? new HarnessAnswer(read.Bytes, null, read.Name, false, watch.Elapsed)
                    : new HarnessAnswer(null, null, null, true, watch.Elapsed);
            }
            catch (Exception e)
            {
                return new HarnessAnswer(null, $"{e.GetType().Name}: {e.Message}", null, false, watch.Elapsed);
            }
        }

        /// <summary>
        /// A static class <c>Harness</c> with <c>Types</c> (type name → read the bytes, write the value back) and
        /// <c>Registries</c> (registry → read the packet with the id, give its name and write it back; null for an unknown id).
        /// </summary>
        private static string Source(CSharpOptions options, IReadOnlyCollection<string> types, IReadOnlyCollection<string> registries, Func<string, string> className)
        {
            var stream = options.Io == CSharpIo.Stream;
            var builder = new StringBuilder();
            builder.AppendLine($"using {options.RuntimeNamespace};");
            builder.AppendLine("public static class Harness {");
            builder.AppendLine("  public static readonly global::System.Collections.Generic.Dictionary<string, global::System.Func<byte[], byte[]>> Types = new() {");
            foreach (var type in types) builder.AppendLine($"    [\"{type}\"] = {TypeMethod(type)},");
            builder.AppendLine("  };");
            builder.AppendLine("  public static readonly global::System.Collections.Generic.Dictionary<string, global::System.Func<uint, byte[], (string Name, byte[] Bytes)?>> Registries = new() {");
            foreach (var registry in registries) builder.AppendLine($"    [\"{registry}\"] = {RegistryMethod(registry)},");
            builder.AppendLine("  };");

            // A length of -1 reads like a network stream: counts and the end are only known from the data.
            var newReader = stream ? $"new {options.ReaderType}(new global::System.IO.MemoryStream(data), -1)" : $"new {options.ReaderType}(data)";
            var pass = stream ? "reader" : "ref reader";
            var checkEnd = stream
                ? "if (!reader.AtEnd) throw new global::System.Exception(\"bytes left\");"
                : "if (reader.Remaining != 0) throw new global::System.Exception($\"{reader.Remaining} bytes left\");";
            var write = stream
                ? $"var buffer = new global::System.IO.MemoryStream(); value.Write(new {options.WriterType}(buffer)); return buffer.ToArray();"
                : $"var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(); var writer = new {options.WriterType}(buffer); value.Write(ref writer); return buffer.WrittenSpan.ToArray();";
            foreach (var type in types)
            {
                builder.AppendLine($$"""
                      static byte[] {{TypeMethod(type)}}(byte[] data) {
                        var reader = {{newReader}};
                        var value = global::{{options.Namespace}}.{{className(type)}}.Read({{pass}});
                        {{checkEnd}}
                        {{write}}
                      }
                    """);
            }

            foreach (var registry in registries)
            {
                var registryClass = $"global::{options.Namespace}.{RegistryClass(options.Registry, registry)}";
                builder.AppendLine($$"""
                      static (string Name, byte[] Bytes)? {{RegistryMethod(registry)}}(uint id, byte[] data) {
                        var reader = {{newReader}};
                        var visitor = new PacketWriter();
                        if (!{{registryClass}}.Dispatch(id, {{pass}}, ref visitor)) return null;
                        {{checkEnd}}
                        return ({{registryClass}}.NameOf(id) ?? "", visitor.Bytes ?? []);
                      }
                    """);
            }

            if (registries.Count > 0)
            {
                var writePacket = stream
                    ? $"var buffer = new global::System.IO.MemoryStream(); packet.Write(new {options.WriterType}(buffer)); Bytes = buffer.ToArray();"
                    : $"var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(); var writer = new {options.WriterType}(buffer); packet.Write(ref writer); Bytes = buffer.WrittenSpan.ToArray();";
                builder.AppendLine($$"""
                      struct PacketWriter : {{options.VisitorInterface}} {
                        public byte[]? Bytes;
                        public void Visit<T>(ref T packet) where T : {{options.PacketInterface}}<T> {
                          {{writePacket}}
                        }
                      }
                    """);
            }

            builder.AppendLine("}");
            return builder.ToString();
        }
    }

    private sealed class JavaHarness : TargetHarness
    {
        private readonly JavaToolchain _java;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "format-studio-java-" + Guid.NewGuid().ToString("N"));
        private readonly string _classes;

        /// <param name="className">The generated class of a type name.</param>
        public JavaHarness(GeneratedOutput output, JavaOptions options, JavaToolchain java, IReadOnlyCollection<string> types, IReadOnlyCollection<string> registries,
            Func<string, string> className)
        {
            _java = java;
            try
            {
                _classes = java.Compile(output, Source(options, types, registries, className), _directory);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// One line per job, <c>T type hex</c> or <c>R registry id hex</c> (<c>-</c> for the registry of a project without
        /// protocols); the answers are <c>ok nanos packet hex</c>, <c>err nanos message</c> or <c>unknown nanos</c>.
        /// </summary>
        public override IReadOnlyList<HarnessAnswer> Run(IReadOnlyList<HarnessJob> jobs)
        {
            if (jobs.Count == 0) return [];
            var input = new StringBuilder();
            foreach (var job in jobs)
            {
                input.Append(job.Type != null
                    ? $"T {job.Type} {Convert.ToHexString(job.Bytes)}\n"
                    : $"R {(job.Registry!.Length == 0 ? "-" : job.Registry)} {job.Id} {Convert.ToHexString(job.Bytes)}\n");
            }

            var lines = _java.Run(_classes, "Harness", input.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != jobs.Count) throw new GeneratedCodeException($"The Java harness answered {lines.Length} of {jobs.Count} jobs");
            return lines.Select(Answer).ToList();
        }

        private static HarnessAnswer Answer(string line)
        {
            var parts = line.TrimEnd('\r').Split(' ', 4);
            var duration = TimeSpan.FromTicks(long.Parse(parts[1], CultureInfo.InvariantCulture) / 100);
            return parts[0] switch
            {
                "ok" => new HarnessAnswer(Convert.FromHexString(parts[3]), null, parts[2] == "-" ? null : parts[2], false, duration),
                "unknown" => new HarnessAnswer(null, null, null, true, duration),
                _ => new HarnessAnswer(null, string.Join(' ', parts.Skip(2)), null, false, duration),
            };
        }

        public override void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string Source(JavaOptions options, IReadOnlyCollection<string> types, IReadOnlyCollection<string> registries, Func<string, string> className)
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
                            String[] parts = line.split(" ", -1);
                            long start = System.nanoTime();
                            try {
                                String answer = parts[0].equals("T")
                                    ? "- " + toHex(roundTrip(parts[1], fromHex(parts[2])))
                                    : dispatch(parts[1], Long.parseLong(parts[2]), fromHex(parts[3]));
                                long time = System.nanoTime() - start;
                                out.println(answer == null ? "unknown " + time : "ok " + time + " " + answer);
                            } catch (Exception e) {
                                long time = System.nanoTime() - start;
                                out.println("err " + time + " " + e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()).replace('\n', ' '));
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

                    static byte[] write(FormatSerializable value, FormatReader reader) throws IOException {
                        if (!reader.atEnd()) throw new IllegalStateException("bytes left");
                        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                        value.write(new FormatWriter(buffer));
                        return buffer.toByteArray();
                    }

                    static byte[] roundTrip(String type, byte[] data) throws IOException {
                        // No length: reads like a network stream, counts and the end are only known from the data.
                        FormatReader reader = new FormatReader(new ByteArrayInputStream(data));
                """);
            // An if per type: a switch whose only label is a throwing default would leave the code after it unreachable.
            foreach (var type in types)
                builder.AppendLine($"        if (type.equals(\"{type}\")) return write({options.Package}.{className(type)}.read(reader), reader);");
            builder.AppendLine("""
                        throw new IllegalArgumentException(type);
                    }

                    /** The packet's name and hex, or null for an id the registry doesn't know. */
                    static String dispatch(String registry, long id, byte[] data) throws IOException {
                        // The registries take an int id; a larger one is no packet's.
                        if (id > Integer.MAX_VALUE) return null;
                        FormatReader reader = new FormatReader(new ByteArrayInputStream(data));
                """);
            foreach (var registry in registries)
            {
                var registryClass = $"{options.Package}.{RegistryClass(options.Registry, registry)}";
                builder.AppendLine($"        if (registry.equals(\"{(registry.Length == 0 ? "-" : registry)}\")) " +
                                   $"return packet({registryClass}.read((int) id, reader), {registryClass}.nameOf((int) id), reader);");
            }

            builder.AppendLine("""
                        throw new IllegalArgumentException(registry);
                    }

                    static String packet(FormatPacket packet, String name, FormatReader reader) throws IOException {
                        return packet == null ? null : name + " " + toHex(write(packet, reader));
                    }
                }
                """);
            return builder.ToString();
        }
    }
}
