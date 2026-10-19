using System.Text.Json;
using System.Text.Json.Nodes;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Web;

/// <summary>The <c>expect</c> of a test case as the case page edits it.</summary>
public sealed class ExpectDraft
{
    /// <summary>The case must fail instead of round-tripping.</summary>
    public bool MustFail { get; set; }

    public bool CheckValue { get; set; }
    public string ValueJson { get; set; } = "";
    /// <summary><c>value: auto</c>: filled in by Accept actual.</summary>
    public bool ValueAuto { get; set; }
    public bool Subset { get; set; }
    public bool CheckWarnings { get; set; }
    /// <summary>One expected warning per line; none checks that there are no warnings.</summary>
    public string WarningsText { get; set; } = "";
    public int? Consumed { get; set; }

    public ExpectedFailure? Fails { get; set; }
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";

    public static ExpectDraft From(TestExpectation? expect) => expect == null ? new ExpectDraft() : new ExpectDraft
    {
        MustFail = expect.ExpectsFailure,
        CheckValue = expect.HasValue,
        ValueAuto = expect.ValueAuto,
        ValueJson = expect is { HasValue: true, ValueAuto: false } ? JsonValues.ToText(expect.Value) : "",
        Subset = expect.Match == MatchMode.Subset,
        CheckWarnings = expect.Warnings != null,
        WarningsText = string.Join("\n", expect.Warnings ?? []),
        Consumed = expect.Consumed,
        Fails = expect.Fails,
        Error = expect.Error ?? "",
        Path = expect.Path ?? "",
    };

    /// <summary>The expectation, or null when nothing is expected beyond a round trip.</summary>
    /// <param name="hasBytes">The case has bytes, so that by default it must fail to decode them, else to encode its value.</param>
    /// <exception cref="JsonException">The expected value isn't JSON.</exception>
    public TestExpectation? ToExpectation(bool hasBytes)
    {
        TestExpectation expect;
        if (MustFail)
        {
            expect = new TestExpectation
            {
                // A case that must fail needs one of these; without text or field any error of the step will do.
                Fails = Fails ?? (string.IsNullOrWhiteSpace(Error) && string.IsNullOrWhiteSpace(Path)
                    ? hasBytes ? ExpectedFailure.Decode : ExpectedFailure.Encode
                    : null),
                Error = string.IsNullOrWhiteSpace(Error) ? null : Error.Trim(),
                Path = string.IsNullOrWhiteSpace(Path) ? null : Path.Trim(),
            };
        }
        else
        {
            expect = new TestExpectation
            {
                HasValue = CheckValue,
                ValueAuto = CheckValue && ValueAuto,
                Value = !CheckValue ? null : ValueAuto ? TestCaseDef.Auto : JsonNode.Parse(string.IsNullOrWhiteSpace(ValueJson) ? "null" : ValueJson),
                Match = Subset ? MatchMode.Subset : MatchMode.Exact,
                Warnings = CheckWarnings ? WarningsText.Split('\n').Select(w => w.Trim()).Where(w => w.Length > 0).ToList() : null,
                Consumed = Consumed,
            };
        }

        return expect.IsEmpty ? null : expect;
    }
}
