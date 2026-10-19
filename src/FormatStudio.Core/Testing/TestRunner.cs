using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Testing;

public enum CaseFailureKind
{
    /// <summary>The sample itself is broken: its type or hex can't be read, or it has neither value nor hex.</summary>
    Invalid,
    /// <summary>The value doesn't encode.</summary>
    Encode,
    /// <summary>The value encodes to other bytes than the expected hex.</summary>
    Mismatch,
    /// <summary>The bytes don't decode, or don't encode back to themselves.</summary>
    RoundTrip,
}

/// <summary>Why a test failed.</summary>
/// <param name="Diagnostic">The simulator's diagnostic for encode and round trip failures.</param>
public sealed record CaseFailure(CaseFailureKind Kind, string Message, Diagnostic? Diagnostic = null)
{
    /// <summary>The expected bytes of a <see cref="CaseFailureKind.Mismatch"/>.</summary>
    public byte[]? Expected { get; init; }
    /// <summary>The bytes the value encoded to, for a <see cref="CaseFailureKind.Mismatch"/>.</summary>
    public byte[]? Actual { get; init; }
    /// <summary>The first offset at which <see cref="Expected"/> and <see cref="Actual"/> differ.</summary>
    public int? Offset { get; init; }
}

/// <summary>The outcome of one sample.</summary>
/// <param name="Bytes">The bytes that were round-tripped: the encoded value, else the expected hex.</param>
public sealed record CaseResult(SampleDef Sample, IReadOnlyList<CaseFailure> Failures, byte[]? Bytes)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>"ok" or the first failure in a few words, for lists of results.</summary>
    public string Summary => Failures.FirstOrDefault() switch
    {
        null => "ok",
        { Kind: CaseFailureKind.Mismatch, Actual: { } actual } => $"differs: {Hex.Format(actual)}",
        { Kind: CaseFailureKind.Encode } failure => "encode failed: " + failure.Message,
        { Kind: CaseFailureKind.RoundTrip } failure => "round trip failed: " + failure.Message,
        var failure => failure.Message,
    };
}

/// <summary>
/// Runs a project's samples against the simulator: a value must encode (to its hex, when both are given), and the
/// bytes must decode and encode back to themselves. The CLI, the web UI and tests all run samples through here.
/// </summary>
public static class TestRunner
{
    public static IReadOnlyList<CaseResult> Run(FormatProject project, Func<SampleDef, bool>? filter = null)
    {
        var codec = new Codec(project);
        return project.Samples.Where(s => filter?.Invoke(s) ?? true).Select(s => Run(codec, s)).ToList();
    }

    public static CaseResult Run(FormatProject project, SampleDef sample) => Run(new Codec(project), sample);

    public static CaseResult Run(Codec codec, SampleDef sample)
    {
        var failures = new List<CaseFailure>();
        TypeRef type;
        byte[]? expected;
        try
        {
            type = codec.Project.Normalize(Codec.ParseType(sample.Type));
            expected = sample.Hex != null ? Hex.Parse(sample.Hex) : null;
        }
        catch (Exception e) when (e is System.FormatException or FormatDefinitionException)
        {
            return new CaseResult(sample, [new CaseFailure(CaseFailureKind.Invalid, e.Message)], null);
        }

        if (sample.Value == null && expected == null)
            return new CaseResult(sample, [new CaseFailure(CaseFailureKind.Invalid, "no value or hex")], null);

        byte[]? bytes = null;
        if (sample.Value != null)
        {
            var encoded = codec.Encode(type, sample.Value);
            failures.AddRange(Errors(encoded.Diagnostics, CaseFailureKind.Encode));
            if (encoded.Success)
            {
                bytes = encoded.Bytes;
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

        bytes ??= expected;
        if (bytes != null)
        {
            var roundTrip = Simulator.RoundTripBytes(codec, type, bytes);
            failures.AddRange(Errors(roundTrip.Diagnostics, CaseFailureKind.RoundTrip));
        }

        return new CaseResult(sample, failures, bytes);
    }

    private static IEnumerable<CaseFailure> Errors(DiagnosticBag diagnostics, CaseFailureKind kind) => diagnostics
        .Where(d => d.Severity == Severity.Error)
        .Select(d => new CaseFailure(kind, d.Message, d));
}
