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

    /// <summary>The failure as one line: the diagnostic it comes from, or its message.</summary>
    public override string ToString() => Diagnostic?.ToString() ?? (Path != null ? $"{Message} [{Path}]" : Message);
}

/// <summary>The outcome of one test case.</summary>
/// <param name="Bytes">The bytes that were round-tripped: the encoded value, else the case's bytes.</param>
public sealed record CaseResult(TestCaseDef Case, IReadOnlyList<CaseFailure> Failures, byte[]? Bytes)
{
    public bool Skipped { get; init; }
    public TimeSpan Duration { get; init; }

    public CaseStatus Status => Skipped ? CaseStatus.Skipped : Failures.Count == 0 ? CaseStatus.Passed : CaseStatus.Failed;
    public bool Passed => Status == CaseStatus.Passed;
    public bool Failed => Status == CaseStatus.Failed;

    /// <summary>"ok", "skipped: …" or the first failure in a few words, for lists of results.</summary>
    public string Summary => Skipped ? "skipped: " + Case.Skip : Failures.FirstOrDefault() switch
    {
        null => "ok",
        { Kind: CaseFailureKind.Mismatch, Actual: { } actual } => $"differs: {Hex.Format(actual)}",
        { Kind: CaseFailureKind.Encode } failure => "encode failed: " + failure.Message,
        { Kind: CaseFailureKind.RoundTrip } failure => "round trip failed: " + failure.Message,
        var failure => failure.Path != null ? $"{failure.Path}: {failure.Message}" : failure.Message,
    };
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
        && (Types.Count == 0 || Types.Contains(test.Type))
        && (Files.Count == 0 || test.Source?.File is { } file && Files.Any(f => Glob(f, file)));

    private static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
}

/// <summary>
/// Runs a project's test cases against the simulator. A value must encode (to the case's bytes, when both are given),
/// and the bytes must decode and encode back to themselves; <see cref="TestCaseDef.Expect"/> adds checks of the decoded
/// value, warnings and length, or turns the case into one that must fail. The CLI, the web UI and tests all run cases
/// through here.
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
        TypeRef type;
        byte[]? given;
        try
        {
            type = codec.Project.Normalize(Codec.ParseType(test.Type));
            given = codec.Project.TestBytes(test);
        }
        catch (Exception e) when (e is System.FormatException or FormatDefinitionException or IOException or UnauthorizedAccessException)
        {
            return Invalid(test, e.Message);
        }

        if (test.Value == null && given == null) return Invalid(test, "no value, hex or file");
        var expect = test.Expect ?? new TestExpectation();
        return expect.ExpectsFailure ? RunFailing(codec, test, type, given, expect) : RunPassing(codec, test, type, given, expect);
    }

    private static CaseResult Invalid(TestCaseDef test, string message) => new(test, [new CaseFailure(CaseFailureKind.Invalid, message)], null);

    private static CaseResult RunPassing(Codec codec, TestCaseDef test, TypeRef type, byte[]? given, TestExpectation expect)
    {
        var failures = new List<CaseFailure>();
        var warnings = new List<Diagnostic>();
        byte[]? bytes = null;
        if (test.Value != null)
        {
            var encoded = codec.Encode(type, test.Value);
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
                var decoded = codec.Decode(type, bytes);
                if (decoded.Success && decoded.Consumed != consumed)
                    failures.Add(new CaseFailure(CaseFailureKind.Consumed, $"decoding takes up {decoded.Consumed} bytes, expected {consumed}"));
                bytes = bytes[..Math.Min(consumed, bytes.Length)];
            }

            var roundTrip = Simulator.RoundTripBytes(codec, type, bytes);
            failures.AddRange(Errors(roundTrip.Diagnostics, CaseFailureKind.RoundTrip));
            warnings.AddRange(roundTrip.Diagnostics.Where(d => d.Severity == Severity.Warning));
            if (expect.HasValue && roundTrip.Success)
                failures.AddRange(Differences(expect.Value, roundTrip.Value, expect.Match == MatchMode.Subset));
        }

        if (expect.Warnings is { } patterns) failures.AddRange(CheckWarnings(warnings, patterns));
        return new CaseResult(test, failures, bytes);
    }

    private static CaseResult RunFailing(Codec codec, TestCaseDef test, TypeRef type, byte[]? given, TestExpectation expect)
    {
        var step = expect.Fails ?? (given != null ? ExpectedFailure.Decode : ExpectedFailure.Encode);
        DiagnosticBag diagnostics;
        if (step == ExpectedFailure.Encode)
        {
            if (test.Value == null) return Invalid(test, "'fails: encode' needs a value");
            diagnostics = codec.Encode(type, test.Value).Diagnostics;
        }
        else
        {
            if (given == null) return Invalid(test, "'fails: decode' needs a hex or file");
            diagnostics = codec.Decode(type, given).Diagnostics;
        }

        var errors = diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        var verb = step == ExpectedFailure.Encode ? "encodes" : "decodes";
        if (errors.Count == 0)
            return new CaseResult(test, [new CaseFailure(CaseFailureKind.ExpectedFailure, $"{verb} without errors, but should fail")], given);
        if (errors.Any(e => ErrorMatches(e, expect))) return new CaseResult(test, [], given);

        var wanted = string.Join(" ", new[] { expect.Error is { } text ? $"'{text}'" : null, expect.Path is { } path ? $"at {path}" : null }.OfType<string>());
        return new CaseResult(test, [new CaseFailure(CaseFailureKind.ExpectedFailure, $"fails with other errors than {wanted}: {errors[0].Message}", errors[0])], given);
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
}
