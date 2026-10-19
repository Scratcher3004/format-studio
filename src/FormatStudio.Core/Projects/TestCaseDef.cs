using System.Text.Json.Nodes;
using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>
/// One test case of a project's test files. Without <see cref="Expect"/> it is what a sample always was: the value must
/// encode (to the hex, when both are given) and the bytes must decode and encode back to themselves.
/// </summary>
public sealed class TestCaseDef
{
    public required string Name { get; set; }
    /// <summary>A type expression; the suite's type when the case has none.</summary>
    public required string Type { get; set; }
    public JsonNode? Value { get; set; }
    /// <summary>The expected encoding, if known.</summary>
    public string? Hex { get; set; }
    /// <summary>A file with the expected encoding instead of <see cref="Hex"/>, relative to the test file.</summary>
    public string? File { get; set; }
    public TestExpectation? Expect { get; set; }
    /// <summary>Why the case is skipped, or null when it runs.</summary>
    public string? Skip { get; set; }
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
    public bool HasBytes => Hex != null || File != null;
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
