using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Testing;

public enum CaseFailureKind
{
    /// <summary>The case itself is broken: its type, hex or file can't be read, or it has neither value nor bytes.</summary>
    Invalid,
    /// <summary>The value doesn't encode.</summary>
    Encode,
    /// <summary>The value encodes to other bytes than the expected ones.</summary>
    Mismatch,
    /// <summary>The bytes don't decode, or don't encode back to themselves.</summary>
    RoundTrip,
    /// <summary>The decoded value isn't the expected one.</summary>
    Value,
    /// <summary>Decoding took up another number of bytes than expected.</summary>
    Consumed,
    /// <summary>Other warnings than the expected ones.</summary>
    Warnings,
    /// <summary>The case was expected to fail, but succeeded or failed with another error.</summary>
    ExpectedFailure,
    /// <summary>Random values of a type don't round-trip.</summary>
    Fuzz,
    /// <summary>The hex or the expected value is still <c>auto</c>, to be filled in by <c>test --update</c>.</summary>
    Snapshot,
}

public enum CaseStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Why a test case failed.</summary>
/// <param name="Diagnostic">The simulator's diagnostic, for failures that come from one.</param>
public sealed record CaseFailure(CaseFailureKind Kind, string Message, Diagnostic? Diagnostic = null)
{
    /// <summary>The expected bytes of a <see cref="CaseFailureKind.Mismatch"/>.</summary>
    public byte[]? Expected { get; init; }
    /// <summary>The bytes the value encoded to, for a <see cref="CaseFailureKind.Mismatch"/>.</summary>
    public byte[]? Actual { get; init; }
    /// <summary>The first offset at which <see cref="Expected"/> and <see cref="Actual"/> differ.</summary>
    public int? Offset { get; init; }
    /// <summary>The field the failure is about, e.g. <c>location.x</c>.</summary>
    public string? Path { get; init; }
    /// <summary>The random value of a <see cref="CaseFailureKind.Fuzz"/> failure, to repeat it with.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>The failure as one line: the diagnostic it comes from, or its message.</summary>
    public override string ToString() => Diagnostic?.ToString() ?? (Path != null ? $"{Message} [{Path}]" : Message);
}

/// <summary>The outcome of one test case.</summary>
/// <param name="Bytes">The bytes that were round-tripped: the encoded value, else the case's bytes.</param>
public sealed record CaseResult(TestCaseDef Case, IReadOnlyList<CaseFailure> Failures, byte[]? Bytes)
{
    public bool Skipped { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>The value <see cref="Bytes"/> decoded to, when they did.</summary>
    public JsonNode? Decoded { get; init; }
    /// <summary>The warnings of encoding and decoding.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
    /// <summary>The bytes decoding took up, for a case that expects a number of them.</summary>
    public int? Consumed { get; init; }
    /// <summary>The codegen target whose generated code the case ran against; null for the simulator.</summary>
    public string? Target { get; init; }
    /// <summary>Why a skipped case didn't run, when that isn't its own <c>skip</c>, e.g. a type without generated code.</summary>
    public string? SkipReason { get; init; }

    /// <summary><c>suite/name</c>, and <c>suite/name [target]</c> against generated code.</summary>
    public string Id => IdOf(Case, Target);

    public static string IdOf(TestCaseDef test, string? target) => target == null ? $"{test.Suite}/{test.Name}" : $"{test.Suite}/{test.Name} [{target}]";

    public CaseStatus Status => Skipped ? CaseStatus.Skipped : Failures.Count == 0 ? CaseStatus.Passed : CaseStatus.Failed;
    public bool Passed => Status == CaseStatus.Passed;
    public bool Failed => Status == CaseStatus.Failed;

    /// <summary>"ok", "skipped: …" or the first failure in a few words, for lists of results.</summary>
    public string Summary => Skipped ? "skipped: " + (SkipReason ?? Case.Skip) : Failures.FirstOrDefault() switch
    {
        null => "ok",
        { Kind: CaseFailureKind.Mismatch, Actual: { } actual } => $"differs: {Hex.Format(actual)}",
        { Kind: CaseFailureKind.Encode } failure => "encode failed: " + failure.Message,
        { Kind: CaseFailureKind.RoundTrip } failure => "round trip failed: " + failure.Message,
        var failure => failure.Path != null ? $"{failure.Path}: {failure.Message}" : failure.Message,
    };

    /// <summary>The failures one per line, with the random values of fuzz failures to repeat them with; empty when it passed.</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        foreach (var problem in Failures.Select(f => f.ToString()).Distinct()) text.AppendLine(problem);
        foreach (var failure in Failures.Where(f => f.Value != null).Take(3))
            text.AppendLine($"  value: {JsonValues.ToText(failure.Value, false)}");
        return text.ToString();
    }
}

/// <summary>Which test cases to run; a case runs when it matches every part that is given.</summary>
public sealed record TestFilter
{
    /// <summary>Globs (<c>*</c>, <c>?</c>) of names; a name matches as <c>name</c> or <c>suite/name</c>.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
    /// <summary>Tags; a case with any of them matches.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Types, as written in the cases.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];
    /// <summary>Globs of test files, relative to the project.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public static TestFilter All { get; } = new();

    public bool Matches(TestCaseDef test) =>
        (Names.Count == 0 || Names.Any(n => Glob(n, test.Name) || Glob(n, $"{test.Suite}/{test.Name}")))
        && (Tags.Count == 0 || Tags.Any(test.Tags.Contains))
        && (Types.Count == 0 || test.Type != null && Types.Contains(test.Type))
        && (Files.Count == 0 || test.Source?.File is { } file && Files.Any(f => Glob(f, file)));

    private static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
}

/// <summary>
/// Runs a project's test cases against the simulator. A value must encode (to the case's bytes, when both are given),
/// and the bytes must decode and encode back to themselves; <see cref="TestCaseDef.Expect"/> adds checks of the decoded
/// value, warnings and length, or turns the case into one that must fail. A packet case's bytes start with the packet's
/// id, and a fuzz case round-trips random values. The CLI, the web UI and tests all run cases through here.
/// </summary>
public static class TestRunner
{
    /// <summary>Value differences reported per case; more are summed up in one line.</summary>
    private const int MaxDifferences = 10;

    public static IReadOnlyList<CaseResult> Run(FormatProject project, TestFilter? filter = null)
    {
        var codec = new Codec(project);
        return project.Tests.Where((filter ?? TestFilter.All).Matches).Select(t => Run(codec, t)).ToList();
    }

    public static CaseResult Run(FormatProject project, TestCaseDef test) => Run(new Codec(project), test);

    public static CaseResult Run(Codec codec, TestCaseDef test)
    {
        if (test.Skip != null) return new CaseResult(test, [], null) { Skipped = true };
        var watch = Stopwatch.StartNew();
        var result = RunCase(codec, test);
        return result with { Duration = watch.Elapsed };
    }

    private static CaseResult RunCase(Codec codec, TestCaseDef test)
    {
        Subject subject;
        byte[]? given;
        try
        {
            subject = Subject.For(codec, test);
            given = codec.Project.TestBytes(test);
        }
        catch (Exception e) when (e is System.FormatException or FormatDefinitionException or IOException or UnauthorizedAccessException)
        {
            return Invalid(test, e.Message);
        }

        if (test.Fuzz is { } fuzz)
        {
            return test.Value != null || given != null || test.Expect != null
                ? Invalid(test, "a fuzz case has no value, hex, file or expect of its own")
                : RunFuzz(codec.Project, test, subject, fuzz);
        }

        if (test.Value == null && given == null) return Invalid(test, "no value, hex or file");
        if (test.Value != null && subject.Type == null) return Invalid(test, "a packet case with a value needs a type to encode it as");
        var expect = test.Expect ?? new TestExpectation();
        return expect.ExpectsFailure ? RunFailing(test, subject, given, expect) : RunPassing(test, subject, given, expect);
    }

    private static CaseResult Invalid(TestCaseDef test, string message) => new(test, [new CaseFailure(CaseFailureKind.Invalid, message)], null);

    private static CaseResult RunPassing(TestCaseDef test, Subject subject, byte[]? given, TestExpectation expect)
    {
        var failures = new List<CaseFailure>();
        var warnings = new List<Diagnostic>();
        byte[]? bytes = null;
        JsonNode? decodedValue = null;
        int? actualConsumed = null;
        if (test.Value != null)
        {
            var encoded = subject.Encode(subject.Type!, test.Value);
            failures.AddRange(Errors(encoded.Diagnostics, CaseFailureKind.Encode));
            warnings.AddRange(encoded.Diagnostics.Where(d => d.Severity == Severity.Warning));
            if (encoded.Success)
            {
                bytes = encoded.Bytes;
                // With an expected length, the given bytes may go on after the value.
                var expected = given != null && expect.Consumed is { } length && length <= given.Length ? given[..length] : given;
                if (expected != null && !expected.AsSpan().SequenceEqual(bytes))
                {
                    failures.Add(new CaseFailure(CaseFailureKind.Mismatch, $"encodes to {Hex.Format(bytes)}, expected {Hex.Format(expected)}")
                    {
                        Expected = expected,
                        Actual = bytes,
                        Offset = expected.AsSpan().CommonPrefixLength(bytes),
                    });
                }
            }
        }

        // The case's own bytes go first when there is a length to check, since an encoded value has no rest after it.
        bytes = expect.Consumed != null ? given ?? bytes : bytes ?? given;
        if (bytes != null)
        {
            if (expect.Consumed is { } consumed)
            {
                var decoded = subject.Decode(bytes);
                if (decoded.Success) actualConsumed = decoded.Consumed;
                if (decoded.Success && decoded.Consumed != consumed)
                    failures.Add(new CaseFailure(CaseFailureKind.Consumed, $"decoding takes up {decoded.Consumed} bytes, expected {consumed}"));
                // The rest of the checks are of the bytes the type takes up, also when that isn't the expected number.
                bytes = bytes[..Math.Min(decoded.Success ? decoded.Consumed : consumed, bytes.Length)];
            }

            var roundTrip = subject.RoundTrip(bytes);
            failures.AddRange(Errors(roundTrip.Diagnostics, CaseFailureKind.RoundTrip));
            warnings.AddRange(roundTrip.Diagnostics.Where(d => d.Severity == Severity.Warning));
            if (roundTrip.Success) decodedValue = roundTrip.Value;
            if (expect.ValueAuto) failures.Add(new CaseFailure(CaseFailureKind.Snapshot, "the expected value is auto; format-studio test --update fills it in"));
            else if (expect.HasValue && roundTrip.Success)
                failures.AddRange(Differences(expect.Value, roundTrip.Value, expect.Match == MatchMode.Subset));
        }

        if (test.HexAuto && bytes != null)
            failures.Add(new CaseFailure(CaseFailureKind.Snapshot, "the hex is auto; format-studio test --update fills it in") { Actual = bytes });
        if (expect.Warnings is { } patterns) failures.AddRange(CheckWarnings(warnings, patterns));
        return new CaseResult(test, failures, bytes) { Decoded = decodedValue, Warnings = warnings, Consumed = actualConsumed };
    }

    private static CaseResult RunFailing(TestCaseDef test, Subject subject, byte[]? given, TestExpectation expect)
    {
        var step = expect.Fails ?? (given != null ? ExpectedFailure.Decode : ExpectedFailure.Encode);
        DiagnosticBag diagnostics;
        if (step == ExpectedFailure.Encode)
        {
            if (test.Value == null) return Invalid(test, "'fails: encode' needs a value");
            diagnostics = subject.Encode(subject.Type!, test.Value).Diagnostics;
        }
        else
        {
            if (given == null) return Invalid(test, "'fails: decode' needs a hex or file");
            diagnostics = subject.Decode(given).Diagnostics;
        }

        var errors = diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        var verb = step == ExpectedFailure.Encode ? "encodes" : "decodes";
        if (errors.Count == 0)
            return new CaseResult(test, [new CaseFailure(CaseFailureKind.ExpectedFailure, $"{verb} without errors, but should fail")], given);
        if (errors.Any(e => ErrorMatches(e, expect))) return new CaseResult(test, [], given);

        var wanted = string.Join(" ", new[] { expect.Error is { } text ? $"'{text}'" : null, expect.Path is { } path ? $"at {path}" : null }.OfType<string>());
        return new CaseResult(test, [new CaseFailure(CaseFailureKind.ExpectedFailure, $"fails with other errors than {wanted}: {errors[0].Message}", errors[0])], given);
    }

    /// <summary>Round-trips random values of the case's type, or of all packets, and reports each type that fails.</summary>
    private static CaseResult RunFuzz(FormatProject project, TestCaseDef test, Subject subject, FuzzOptions fuzz)
    {
        IEnumerable<TypeRef> types = subject.Type != null
            ? [subject.Type]
            : (subject.Packets ?? Simulator.FuzzTypes(project)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new NamedRef(t.Name));
        var failures = new List<CaseFailure>();
        foreach (var type in types)
        {
            var name = type.ToString();
            var failed = 0;
            CaseFailure? first = null;
            for (var i = 0; i < fuzz.Iterations; i++)
            {
                JsonNode? value;
                try
                {
                    value = new RandomValues(project, Simulator.FuzzSeed(fuzz.Seed, name, i)).Generate(type);
                }
                catch (Exception e) when (e is Expressions.ExprException or FormatDefinitionException)
                {
                    failed++;
                    first ??= new CaseFailure(CaseFailureKind.Fuzz, $"random value: {e.Message}");
                    continue;
                }

                var encoded = subject.Encode(type, value);
                var error = encoded.Diagnostics.FirstOrDefault(d => d.Severity == Severity.Error)
                            ?? subject.RoundTrip(encoded.Bytes, type).Diagnostics.FirstOrDefault(d => d.Severity == Severity.Error);
                if (error == null) continue;
                failed++;
                first ??= new CaseFailure(CaseFailureKind.Fuzz, error.Message, error) { Value = value?.DeepClone(), Actual = encoded.Bytes, Path = error.Path };
            }

            if (first != null)
                failures.Add(first with { Message = $"{name}: {failed} of {fuzz.Iterations} round trips fail, e.g. {first.Message}", Diagnostic = null });
        }

        return new CaseResult(test, failures, null);
    }

    private static bool ErrorMatches(Diagnostic error, TestExpectation expect) =>
        (expect.Error == null || error.Message.Contains(expect.Error, StringComparison.OrdinalIgnoreCase))
        && (expect.Path == null || FieldPath(error.Path) == FieldPath(expect.Path));

    /// <summary>A field path without the <c>$</c> of the root, as the simulator's diagnostics may write it.</summary>
    private static string FieldPath(string? path) => path is null or "$" ? "" : path.StartsWith("$.") ? path[2..] : path;

    private static IEnumerable<CaseFailure> Errors(DiagnosticBag diagnostics, CaseFailureKind kind) => diagnostics
        .Where(d => d.Severity == Severity.Error)
        .Select(d => new CaseFailure(kind, d.Message, d));

    private static IEnumerable<CaseFailure> CheckWarnings(List<Diagnostic> warnings, IReadOnlyList<string> patterns)
    {
        var distinct = warnings.DistinctBy(w => (w.Message, w.Path)).ToList();
        foreach (var pattern in patterns.Where(p => !distinct.Any(w => w.Message.Contains(p, StringComparison.OrdinalIgnoreCase))))
            yield return new CaseFailure(CaseFailureKind.Warnings, $"no warning like '{pattern}'");
        foreach (var warning in distinct.Where(w => !patterns.Any(p => w.Message.Contains(p, StringComparison.OrdinalIgnoreCase))))
            yield return new CaseFailure(CaseFailureKind.Warnings, "unexpected warning: " + warning.Message, warning);
    }

    /// <summary>Where the decoded value differs from the expected one.</summary>
    public static IReadOnlyList<CaseFailure> Differences(JsonNode? expected, JsonNode? actual, bool subset)
    {
        var differences = new List<(string Path, string Message)>();
        Compare(expected, actual, "", subset, differences);
        var failures = differences.Take(MaxDifferences)
            .Select(d => new CaseFailure(CaseFailureKind.Value, d.Message) { Path = d.Path.Length == 0 ? "$" : d.Path })
            .ToList();
        if (differences.Count > MaxDifferences)
            failures.Add(new CaseFailure(CaseFailureKind.Value, $"and {differences.Count - MaxDifferences} more differences"));
        return failures;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string path, bool subset, List<(string, string)> differences)
    {
        switch (expected)
        {
            case JsonObject map when actual is JsonObject actualMap:
                foreach (var (key, value) in map)
                {
                    if (!actualMap.ContainsKey(key)) differences.Add((Child(path, key), "missing"));
                    else Compare(value, actualMap[key], Child(path, key), subset, differences);
                }

                if (!subset)
                {
                    foreach (var key in actualMap.Select(p => p.Key).Where(k => !map.ContainsKey(k)))
                        differences.Add((Child(path, key), $"not expected, decoded as {Text(actualMap[key])}"));
                }

                break;
            case JsonArray array when actual is JsonArray actualArray:
                if (array.Count != actualArray.Count)
                {
                    differences.Add((path, $"expected {array.Count} items, decoded {actualArray.Count}"));
                    break;
                }

                for (var i = 0; i < array.Count; i++) Compare(array[i], actualArray[i], $"{path}[{i}]", subset, differences);
                break;
            default:
                if (!ScalarEquals(expected, actual)) differences.Add((path, $"expected {Text(expected)}, decoded {Text(actual)}"));
                break;
        }
    }

    private static string Child(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    /// <summary>Equal scalars, with integers compared exactly and other numbers to a relative 1e-6 (floats lose digits).</summary>
    private static bool ScalarEquals(JsonNode? expected, JsonNode? actual)
    {
        if (expected is JsonValue a && actual is JsonValue b && a.GetValueKind() == JsonValueKind.Number && b.GetValueKind() == JsonValueKind.Number)
        {
            // Values built in code hold a long, an ulong, a double...: compare their JSON text, which reads as any of them.
            string sx = a.ToJsonString(), sy = b.ToJsonString();
            const NumberStyles style = NumberStyles.Float;
            if (decimal.TryParse(sx, style, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(sy, style, CultureInfo.InvariantCulture, out var y))
            {
                if (x == y) return true;
                if (decimal.Truncate(x) == x && decimal.Truncate(y) == y) return false;
                return Math.Abs(x - y) <= 1e-6m * Math.Max(Math.Abs(x), Math.Abs(y));
            }


            var dx = double.Parse(sx, CultureInfo.InvariantCulture);
            var dy = double.Parse(sy, CultureInfo.InvariantCulture);
            if (dx == dy) return true;
            return Math.Abs(dx - dy) <= 1e-6 * Math.Max(Math.Abs(dx), Math.Abs(dy));
        }

        return JsonNode.DeepEquals(expected, actual);
    }

    /// <param name="Root">The decoded spans, after the packet id of a packet case.</param>
    /// <param name="HeaderLength">The length of a packet case's id.</param>
    internal sealed record Decoded(TypeRef? Type, JsonNode? Value, int Consumed, DiagnosticBag Diagnostics, SpanNode? Root = null, int HeaderLength = 0)
    {
        public bool Success => !Diagnostics.HasErrors;
    }

    /// <summary>
    /// What a case encodes and decodes: its type, or for a packet case a packet id and the packet with that id. A packet
    /// case without a type takes the packet its bytes say.
    /// </summary>
    internal sealed class Subject
    {
        private readonly Codec _codec;
        private readonly PacketHeader? _header;
        private readonly string? _protocol;

        private Subject(Codec codec, TypeRef? type, PacketHeader? header, IReadOnlyList<StructDef>? packets, string? protocol)
        {
            _codec = codec;
            Type = type;
            _header = header;
            Packets = packets;
            _protocol = protocol;
        }

        public TypeRef? Type { get; }
        /// <summary>The packets a packet case's id may pick; null for other cases.</summary>
        public IReadOnlyList<StructDef>? Packets { get; }

        public static Subject For(Codec codec, TestCaseDef test)
        {
            var project = codec.Project;
            var type = test.Type is { } text ? project.Normalize(Codec.ParseType(text)) : null;
            if (test.Packet is not { } frame) return new Subject(codec, type, null, null, null);

            var packets = project.Packets;
            if (frame.Protocol != null)
            {
                var protocol = project.FindProtocol(frame.Protocol) ?? throw new FormatDefinitionException($"there is no protocol '{frame.Protocol}'");
                packets = project.PacketsOf(protocol);
            }

            if (frame.Group is { } group) packets = packets.Where(p => p.Groups.Contains(group));
            var subject = new Subject(codec, type, new PacketHeader(frame.Header, frame.IdMask), packets.ToList(), frame.Protocol);
            if (type != null) subject.PacketOf(type);
            return subject;
        }

        private StructDef PacketOf(TypeRef type) =>
            type is NamedRef named && _codec.Project.Find(named.Name) is StructDef { IsPacket: true } packet
                ? packet
                : throw new FormatDefinitionException($"{type} is not a packet, so it has no id to start the bytes with");

        public EncodeResult Encode(TypeRef type, JsonNode? value) =>
            _header == null ? _codec.Encode(type, value) : Simulator.EncodePacket(_codec, PacketOf(type), value, _header);

        /// <summary>Decodes the bytes as <paramref name="type"/> (the case's type by default), or as the packet their id picks.</summary>
        public Decoded Decode(byte[] bytes, TypeRef? type = null)
        {
            type ??= Type;
            if (_header == null)
            {
                var result = _codec.Decode(type ?? throw new InvalidOperationException("A case without a frame has a type"), bytes);
                return new Decoded(type, result.Value, result.Consumed, result.Diagnostics, result.Root);
            }

            var diagnostics = new DiagnosticBag();
            try
            {
                var (packet, id, headerLength, decoded) = Simulator.DecodePacket(_codec, bytes, _header, Packets);
                if (packet == null || decoded == null)
                {
                    diagnostics.Error($"No packet has id {id}" + (_protocol != null ? $" in {_protocol}" : ""));
                    return new Decoded(null, null, headerLength, diagnostics);
                }

                var actual = new NamedRef(packet.Name);
                if (type != null && PacketOf(type) != packet)
                {
                    diagnostics.Error($"The bytes are {packet.Name} (id {id}), not {type}");
                    return new Decoded(actual, null, headerLength, diagnostics);
                }

                return new Decoded(actual, decoded.Value, headerLength + decoded.Consumed, decoded.Diagnostics, decoded.Root, headerLength);
            }
            catch (DecodeException e)
            {
                diagnostics.Error($"Packet id: {e.Message}");
                return new Decoded(null, null, 0, diagnostics);
            }
        }

        /// <summary>Decodes the bytes and encodes the result, which must give the same bytes; see <see cref="Simulator.RoundTripBytes"/>.</summary>
        public RoundTripResult RoundTrip(byte[] bytes, TypeRef? type = null)
        {
            if (_header == null) return Simulator.RoundTripBytes(_codec, type ?? Type!, bytes);

            var decoded = Decode(bytes, type);
            var diagnostics = new DiagnosticBag();
            diagnostics.AddRange(decoded.Diagnostics);
            var name = (decoded.Type ?? type ?? Type)?.ToString() ?? "packet";
            if (!decoded.Success || decoded.Type == null) return new RoundTripResult(name, decoded.Value, bytes, null, diagnostics);
            if (decoded.Consumed != bytes.Length) diagnostics.Error($"Decoding consumed {decoded.Consumed} of {bytes.Length} bytes");

            var second = Encode(decoded.Type, decoded.Value);
            diagnostics.AddRange(second.Diagnostics);
            if (second.Success && !second.Bytes.AsSpan().SequenceEqual(bytes))
            {
                var mismatch = bytes.AsSpan().CommonPrefixLength(second.Bytes);
                diagnostics.Error($"Re-encoding differs at offset {mismatch} ({bytes.Length} vs {second.Bytes.Length} bytes)");
            }

            return new RoundTripResult(name, decoded.Value, bytes, second.Bytes, diagnostics);
        }
    }
}
