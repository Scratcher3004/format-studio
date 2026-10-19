using System.Text.Json.Nodes;
using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>
/// One test case of a project's test files. Without <see cref="Expect"/> it is what a sample always was: the value must
/// encode (to the hex, when both are given) and the bytes must decode and encode back to themselves. With
/// <see cref="Packet"/> the bytes start with a packet id, and with <see cref="Fuzz"/> the case round-trips random values.
/// </summary>
public sealed class TestCaseDef
{
    /// <summary>The placeholder for a hex or an expected value that <c>test --update</c> fills in.</summary>
    public const string Auto = "auto";

    public required string Name { get; set; }
    /// <summary>
    /// A type expression; the suite's type when the case has none. Only packet cases (whose bytes say which packet they
    /// are) and fuzz cases (of all packets) may go without.
    /// </summary>
    public string? Type { get; set; }
    public JsonNode? Value { get; set; }
    /// <summary>The expected encoding, if known; <see cref="Auto"/> to have it filled in from the value.</summary>
    public string? Hex { get; set; }
    /// <summary>A file with the expected encoding instead of <see cref="Hex"/>, relative to the test file.</summary>
    public string? File { get; set; }
    public TestExpectation? Expect { get; set; }
    /// <summary>The bytes are a packet id followed by the packet; the case's or its suite's.</summary>
    public PacketFrame? Packet { get; set; }
    /// <summary>The case round-trips random values instead of a value or bytes of its own.</summary>
    public FuzzOptions? Fuzz { get; set; }
    /// <summary>Why the case is skipped, or null when it runs.</summary>
    public string? Skip { get; set; }
    /// <summary>
    /// The codegen targets or generators whose code the case doesn't run against, with why (<c>skip-targets</c>), e.g.
    /// because Java enums reject values that aren't members ("" without a reason); its suite's as well.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkipTargets { get; set; } = new Dictionary<string, string>();
    /// <summary>The tags of the case and of its suite.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];
    /// <summary>The suite of the case: its file's <c>suite</c>, else the file name without extension.</summary>
    public string Suite { get; set; } = "";
    public SourceLocation? Source { get; set; }
    /// <summary>The position of the case among the cases of its file.</summary>
    public int Index { get; set; }
    /// <summary>Keys the case's entry has that cases don't, e.g. misspelled ones; reported as warnings.</summary>
    public IReadOnlyList<string> UnknownKeys { get; set; } = [];
    /// <summary>The entry the case was read from, so that edits and moves keep the keys they don't change.</summary>
    public JsonObject? Entry { get; set; }

    /// <summary>The bytes the case starts from are given, by hex or file.</summary>
    public bool HasBytes => Hex != null && !HexAuto || File != null;

    /// <summary>The hex is the placeholder <c>auto</c>.</summary>
    public bool HexAuto => Hex == Auto;

    /// <summary>What the case tests, for lists of cases: its type, else the packets it may be.</summary>
    public string Subject => Type ?? (Packet?.Protocol is { } protocol ? $"packet of {protocol}" : Packet != null ? "packet" : "all packets");
}

/// <summary>New expected results for a case, as <c>test --update</c> writes them; what is null stays as it is.</summary>
public sealed record TestUpdate
{
    public string? Hex { get; init; }
    /// <summary>Whether <see cref="Value"/> replaces the expected value; the value itself may be null.</summary>
    public bool SetsValue { get; init; }
    public JsonNode? Value { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public int? Consumed { get; init; }

    /// <summary>The keys it changes, e.g. <c>hex</c> and <c>expect.value</c>.</summary>
    public IReadOnlyList<string> Keys => new[]
    {
        Hex != null ? "hex" : null,
        SetsValue ? "expect.value" : null,
        Warnings != null ? "expect.warnings" : null,
        Consumed != null ? "expect.consumed" : null,
    }.OfType<string>().ToList();
}

/// <summary>How a packet case's bytes start: a packet id, which picks the packet among those of a protocol.</summary>
public sealed class PacketFrame
{
    /// <summary>The keys of a <c>packet</c> mapping.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["header", "protocol", "group", "id-mask"];

    /// <summary>The integer primitive the id is written as, e.g. <c>varint32</c>.</summary>
    public required PrimitiveRef Header { get; set; }
    /// <summary>The protocol whose packets the id is looked up in; all packets when null.</summary>
    public string? Protocol { get; set; }
    /// <summary>Only packets in this group, e.g. a direction.</summary>
    public string? Group { get; set; }
    /// <summary>The bits of the header that are the id, e.g. Bedrock's 10-bit id.</summary>
    public ulong IdMask { get; set; } = ulong.MaxValue;
}

/// <summary>A fuzz case: random values of its type, or of all packets (of its frame's protocol), must round-trip.</summary>
public sealed class FuzzOptions
{
    /// <summary>The keys of a <c>fuzz</c> mapping.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["iterations", "seed"];

    /// <summary>Values per type.</summary>
    public int Iterations { get; set; } = 20;
    /// <summary>The same seed gives the same values, on every machine.</summary>
    public int Seed { get; set; } = 1;
}

public enum MatchMode
{
    /// <summary>The decoded value equals the expected one.</summary>
    Exact,
    /// <summary>The expected value's keys have equal values in the decoded value; other keys are ignored.</summary>
    Subset,
}

public enum ExpectedFailure
{
    Encode,
    Decode,
}

/// <summary>What a test case expects beyond a round trip: a decoded value, warnings, a length, or a failure.</summary>
public sealed class TestExpectation
{
    /// <summary>The keys of an <c>expect</c> mapping.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["value", "match", "error", "path", "fails", "warnings", "consumed"];

    /// <summary>Whether a value is expected; the value itself may be null.</summary>
    public bool HasValue { get; set; }
    public JsonNode? Value { get; set; }
    /// <summary>The value is the placeholder <c>auto</c>, which <c>test --update</c> replaces with the decoded value.</summary>
    public bool ValueAuto { get; set; }
    public MatchMode Match { get; set; } = MatchMode.Exact;
    /// <summary>Text the error must contain.</summary>
    public string? Error { get; set; }
    /// <summary>The field the error must be at, e.g. <c>location.x</c>.</summary>
    public string? Path { get; set; }
    /// <summary>Which step must fail; null to take decode when there are bytes, else encode.</summary>
    public ExpectedFailure? Fails { get; set; }
    /// <summary>Texts of the warnings that must occur (and no others); null when warnings don't matter.</summary>
    public IReadOnlyList<string>? Warnings { get; set; }
    /// <summary>How many of the bytes the type takes up; the rest may be anything.</summary>
    public int? Consumed { get; set; }

    /// <summary>The case must fail rather than round-trip.</summary>
    public bool ExpectsFailure => Fails != null || Error != null || Path != null;
}
