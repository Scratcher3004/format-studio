using System.Reflection;
using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Codegen.CSharp;
using FormatStudio.Codegen.Java;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Runtime;
using FormatStudio.Simulation;

namespace FormatStudio.Testing;

/// <summary>What running cases against generated code needs: the runtime to compile C# against, and a JDK for Java.</summary>
public sealed record TargetTools(Assembly? CSharpRuntime, JavaToolchain? Java)
{
    /// <summary>FormatStudio.Runtime for C#, and the JDK <see cref="JavaToolchain.Find"/> finds.</summary>
    public static TargetTools Find() => new(typeof(FormatReader).Assembly, JavaToolchain.Find());
}

/// <summary>
/// Runs a project's test cases against the generated code of a C# or Java codegen target. The generated code reads each
/// case's bytes (or the bytes the simulator encodes its value to) and writes them back, which must give the same bytes;
/// a case that must fail to decode must make it throw. A packet case's id goes through the generated registry of its
/// protocol, which must pick the same packet. Fuzz cases send the simulator's random values through the generated code.
/// The decoded value, warnings and failing to encode are checked by the simulator only (<see cref="TestRunner"/>), and
/// cases of types without a generated <c>Read</c> of their own (e.g. <c>Slot[]</c>) are skipped.
/// </summary>
public static class TargetRunner
{
    /// <summary>Whether cases can run against the target's code: it generates C# or Java.</summary>
    public static bool Supports(FormatProject project, CodegenTarget target) =>
        project.Generators.Find(target.Generator) is CSharpGenerator or JavaGenerator;

    /// <summary>Runs the cases with one compilation of the target's code, and for Java one JVM.</summary>
    /// <exception cref="GeneratedCodeException">The code doesn't generate or compile, or there is no JDK for a Java target.</exception>
    public static IReadOnlyList<CaseResult> Run(FormatProject project, CodegenTarget target, TargetTools tools, TestFilter? filter = null)
    {
        if (!Supports(project, target))
            throw new GeneratedCodeException($"Target '{target.Name}': tests run against C# and Java targets, not {target.Generator}");
        var output = CodeGeneration.Generate(project, target);
        if (output.Diagnostics.HasErrors) throw new GeneratedCodeException(string.Join("\n", output.Diagnostics.Where(d => d.Severity == Severity.Error)));

        var planner = new Planner(project, target, output);
        var plans = project.Tests.Where((filter ?? TestFilter.All).Matches).Select(planner.For).ToList();
        var jobs = plans.SelectMany(p => p.Jobs).ToList();
        IReadOnlyList<HarnessAnswer> answers = [];
        if (jobs.Count > 0)
        {
            var types = jobs.Select(j => j.Type).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
            var registries = jobs.Select(j => j.Registry).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
            using var harness = TargetHarness.Create(project, target, output, tools, types, registries);
            answers = harness.Run(jobs);
        }

        var results = new List<CaseResult>();
        var next = 0;
        foreach (var plan in plans)
        {
            var mine = answers.Skip(next).Take(plan.Jobs.Count).ToList();
            next += plan.Jobs.Count;
            var result = plan.Finish(mine);
            results.Add(result with { Target = target.Name, Duration = TimeSpan.FromTicks(mine.Sum(a => a.Duration.Ticks)) });
        }

        return results;
    }

    /// <summary>The jobs of one case, and how their answers make its result.</summary>
    private sealed record Plan(IReadOnlyList<HarnessJob> Jobs, Func<IReadOnlyList<HarnessAnswer>, CaseResult> Finish)
    {
        public static Plan Done(CaseResult result) => new([], _ => result);
    }

    /// <summary>
    /// Bytes as the generated code gets them: the job (null with <see cref="Skip"/>), the packet id in front of them
    /// that the harness doesn't read (<see cref="Prefix"/>), and the packet the registry must pick.
    /// </summary>
    private sealed record Framed(HarnessJob? Job, string? Skip, byte[] Prefix, string? Packet, ulong Id)
    {
        public static Framed Skipped(string reason) => new(null, reason, [], null, 0);
    }

    private sealed class Planner(FormatProject project, CodegenTarget target, GeneratedOutput output)
    {
        private readonly Codec _codec = new(project);

        public Plan For(TestCaseDef test)
        {
            if (test.Skip != null) return Plan.Done(new CaseResult(test, [], null) { Skipped = true });
            if ((test.SkipTargets.GetValueOrDefault(target.Name) ?? test.SkipTargets.GetValueOrDefault(target.Generator)) is { } why)
                return Plan.Done(Skip(test, why.Length > 0 ? why : "'skip-targets' names the target"));

            TestRunner.Subject subject;
            byte[]? given;
            try
            {
                subject = TestRunner.Subject.For(_codec, test);
                given = project.TestBytes(test);
            }
            catch (Exception e) when (e is System.FormatException or FormatDefinitionException or IOException or UnauthorizedAccessException)
            {
                return Plan.Done(Invalid(test, e.Message));
            }

            if (test.Fuzz is { } fuzz)
            {
                return test.Value != null || given != null || test.Expect != null
                    ? Plan.Done(Invalid(test, "a fuzz case has no value, hex, file or expect of its own"))
                    : Fuzz(test, subject, fuzz);
            }

            if (test.Value == null && given == null) return Plan.Done(Invalid(test, "no value, hex or file"));
            if (test.Value != null && subject.Type == null) return Plan.Done(Invalid(test, "a packet case with a value needs a type to encode it as"));
            var expect = test.Expect ?? new TestExpectation();
            return expect.ExpectsFailure ? Failing(test, subject, given, expect) : Passing(test, subject, given, expect);
        }

        private static CaseResult Invalid(TestCaseDef test, string message) => new(test, [new CaseFailure(CaseFailureKind.Invalid, message)], null);

        private static CaseResult Skip(TestCaseDef test, string reason) => new(test, [], null) { Skipped = true, SkipReason = reason };

        private Plan Passing(TestCaseDef test, TestRunner.Subject subject, byte[]? given, TestExpectation expect)
        {
            byte[] bytes;
            if (given != null)
            {
                // The type takes up the expected number of bytes; the simulator checks that it does.
                bytes = expect.Consumed is { } length && length <= given.Length ? given[..length] : given;
            }
            else
            {
                var encoded = subject.Encode(subject.Type!, test.Value);
                if (!encoded.Success) return Plan.Done(Skip(test, "the simulator can't encode the value"));
                bytes = encoded.Bytes;
            }

            var framed = Frame(test, subject, bytes, subject.Type);
            if (framed.Job == null) return Plan.Done(Skip(test, framed.Skip!));
            return new Plan([framed.Job], answers => new CaseResult(test, Check(framed, answers[0], bytes).ToList(), bytes));
        }

        private Plan Failing(TestCaseDef test, TestRunner.Subject subject, byte[]? given, TestExpectation expect)
        {
            var step = expect.Fails ?? (given != null ? ExpectedFailure.Decode : ExpectedFailure.Encode);
            if (step == ExpectedFailure.Encode) return Plan.Done(Skip(test, "the generated code is only given bytes; failing to encode is checked by the simulator"));
            if (given == null) return Plan.Done(Invalid(test, "'fails: decode' needs a hex or file"));

            var framed = Frame(test, subject, given, subject.Type);
            if (framed.Job == null) return Plan.Done(Skip(test, framed.Skip!));
            if (framed.Job.Registry == null && test.Type != null && framed.Packet != null && framed.Packet != Name(subject.Type))
                return Plan.Done(Skip(test, "the packet id is read by the simulator only, without a registry for the case's frame"));
            return new Plan([framed.Job], answers =>
            {
                var answer = answers[0];
                // A registry that reads another packet than the case's type rejects the bytes as well.
                var rejected = !answer.Success || framed.Job.Registry != null && test.Type != null && answer.Packet != Name(subject.Type);
                return rejected
                    ? new CaseResult(test, [], given)
                    : new CaseResult(test, [new CaseFailure(CaseFailureKind.ExpectedFailure, "the generated code reads it without errors, but should fail")], given);
            });
        }

        /// <summary>Random values of the case's type, or of all packets, encoded by the simulator and read back by the generated code.</summary>
        private Plan Fuzz(TestCaseDef test, TestRunner.Subject subject, FuzzOptions fuzz)
        {
            IEnumerable<TypeRef> types = subject.Type != null
                ? [subject.Type]
                : (subject.Packets ?? Simulator.FuzzTypes(project)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new NamedRef(t.Name));
            var runs = new List<(string Type, JsonNode? Value, byte[] Bytes, Framed Framed)>();
            string? skipped = null;
            foreach (var type in types)
            {
                var name = type.ToString();
                for (var i = 0; i < fuzz.Iterations; i++)
                {
                    JsonNode? value;
                    try
                    {
                        value = new RandomValues(project, Simulator.FuzzSeed(fuzz.Seed, name, i)).Generate(type);
                    }
                    catch (Exception e) when (e is Expressions.ExprException or FormatDefinitionException)
                    {
                        continue;
                    }

                    // What the simulator can't encode or read back is its own failure, reported by the simulator's run.
                    var encoded = subject.Encode(type, value);
                    if (!encoded.Success) continue;
                    var framed = Frame(test, subject, encoded.Bytes, type);
                    if (framed.Job == null)
                    {
                        skipped ??= framed.Skip;
                        break;
                    }

                    runs.Add((name, value, encoded.Bytes, framed));
                }
            }

            if (runs.Count == 0) return Plan.Done(Skip(test, skipped ?? "no random value encodes"));
            return new Plan(runs.Select(r => r.Framed.Job!).ToList(), answers =>
            {
                var failures = new List<CaseFailure>();
                foreach (var group in runs.Select((run, i) => (run, Problem: Check(runs[i].Framed, answers[i], runs[i].Bytes).FirstOrDefault())).GroupBy(r => r.run.Type))
                {
                    var failed = group.Where(r => r.Problem != null).ToList();
                    if (failed.Count == 0) continue;
                    var (run, problem) = failed[0];
                    failures.Add(new CaseFailure(CaseFailureKind.Fuzz, $"{group.Key}: {failed.Count} of {group.Count()} round trips fail, e.g. {problem!.Message}")
                    {
                        Value = run.Value?.DeepClone(),
                        Actual = run.Bytes,
                    });
                }

                return new CaseResult(test, failures, null);
            });
        }

        /// <summary>
        /// The job for the bytes of a case. A packet case's id is read by the simulator: with a registry for the case's
        /// frame it goes to that, else the bytes after it are read as the packet the simulator picks.
        /// </summary>
        private Framed Frame(TestCaseDef test, TestRunner.Subject subject, byte[] bytes, TypeRef? type)
        {
            if (test.Packet is not { } frame)
            {
                var name = Generated(type!, type == subject.Type ? test.Type : null, out var reason);
                return name == null ? Framed.Skipped(reason!) : new Framed(HarnessJob.OfType(name, bytes), null, [], null, 0);
            }

            StructDef? packet;
            ulong id;
            int headerLength;
            try
            {
                (packet, id, headerLength, _) = Simulator.DecodePacket(_codec, bytes, new PacketHeader(frame.Header, frame.IdMask), subject.Packets);
            }
            catch (DecodeException e)
            {
                return Framed.Skipped($"the simulator can't read the packet id: {e.Message}");
            }

            var prefix = bytes[..headerLength];
            var body = bytes[headerLength..];
            var expected = type != null ? Name(type) : packet?.Name;
            // A registry is generated per protocol, or one for all packets of a project without protocols; a group narrows
            // the packets beyond what a registry has.
            if (frame.Group == null && (frame.Protocol == null) == (project.Settings.Protocols.Count == 0))
                return new Framed(HarnessJob.OfPacket(frame.Protocol ?? "", id, body), null, prefix, expected, id);
            if (expected == null) return Framed.Skipped($"no packet has id {id}");

            var generated = Generated(new NamedRef(expected), null, out var why);
            return generated == null ? Framed.Skipped(why!) : new Framed(HarnessJob.OfType(generated, body), null, prefix, expected, id);
        }

        /// <summary>The failure of a job that should give back <paramref name="bytes"/>, if any.</summary>
        private static IEnumerable<CaseFailure> Check(Framed framed, HarnessAnswer answer, byte[] bytes)
        {
            if (answer.Error != null)
            {
                yield return new CaseFailure(CaseFailureKind.RoundTrip, "the generated code fails: " + answer.Error);
                yield break;
            }

            if (answer.Unknown)
            {
                yield return new CaseFailure(CaseFailureKind.RoundTrip, $"the generated registry has no packet with id {framed.Id}");
                yield break;
            }

            if (framed.Job!.Registry != null && answer.Packet != framed.Packet)
            {
                yield return new CaseFailure(CaseFailureKind.RoundTrip, $"the generated registry reads id {framed.Id} as {answer.Packet}, not {framed.Packet}");
                yield break;
            }

            byte[] actual = [.. framed.Prefix, .. answer.Bytes!];
            if (!actual.AsSpan().SequenceEqual(bytes))
            {
                yield return new CaseFailure(CaseFailureKind.RoundTrip, $"the generated code writes {Hex.Format(bytes)} back as {Hex.Format(actual)}")
                {
                    Expected = bytes,
                    Actual = actual,
                    Offset = bytes.AsSpan().CommonPrefixLength(actual),
                };
            }
        }

        private static string? Name(TypeRef? type) => (type as NamedRef)?.Name;

        /// <summary>The name of the generated type with a <c>Read</c> of its own for <paramref name="type"/>, or null and why not.</summary>
        /// <param name="text">The type as the case writes it, for the reason.</param>
        private string? Generated(TypeRef type, string? text, out string? reason)
        {
            var definition = type is NamedRef named ? project.Find(named.Name) : null;
            while (definition is AliasDef { Target: NamedRef target }) definition = project.Find(target.Name);
            reason = definition switch
            {
                null or not (StructDef or BitfieldDef or VariantDef { Tag: not null }) =>
                    $"{text ?? type.ToString()} has no generated Read of its own (structs, bitfields and variants with a tag have)",
                { Owner: { } library } => $"{definition.Name} is from the library {library.Name}",
                _ when output.FileOf(definition.QualifiedName) == null => $"{definition.Name} has no generated code (skipCodegen)",
                _ => null,
            };
            return reason == null ? definition!.Name : null;
        }
    }
}
