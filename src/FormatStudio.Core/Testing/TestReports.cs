using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FormatStudio.Simulation;

namespace FormatStudio.Testing;

/// <summary>Test results as JSON, or as JUnit XML for CI systems.</summary>
public static class TestReports
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// <c>{project, passed, failed, skipped, cases: [{suite, name, type, file, line, tags, status, durationMs, failures}]}</c>;
    /// a failure has its kind and message, and when known its path, offset and expected and actual hex.
    /// </summary>
    public static string Json(string project, IReadOnlyList<CaseResult> results)
    {
        var report = new JsonObject
        {
            ["project"] = project,
            ["passed"] = results.Count(r => r.Passed),
            ["failed"] = results.Count(r => r.Failed),
            ["skipped"] = results.Count(r => r.Skipped),
            ["cases"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["suite"] = r.Case.Suite,
                ["name"] = r.Case.Name,
                ["type"] = r.Case.Type,
                ["file"] = r.Case.Source?.File,
                ["line"] = r.Case.Source?.Line,
                ["tags"] = new JsonArray(r.Case.Tags.Select(t => (JsonNode)t).ToArray()),
                ["status"] = r.Status.ToString().ToLowerInvariant(),
                ["skip"] = r.Case.Skip,
                ["durationMs"] = Math.Round(r.Duration.TotalMilliseconds, 3),
                ["failures"] = new JsonArray(r.Failures.Select(Failure).ToArray()),
            }).ToArray()),
        };
        return report.ToJsonString(Indented);
    }

    private static JsonNode Failure(CaseFailure failure)
    {
        var node = new JsonObject
        {
            ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(failure.Kind.ToString()),
            ["message"] = failure.Message,
        };
        if ((failure.Path ?? failure.Diagnostic?.Path) is { } path) node["path"] = path;
        if (failure.Offset is { } offset) node["offset"] = offset;
        if (failure.Expected is { } expected) node["expected"] = Hex.Format(expected);
        if (failure.Actual is { } actual) node["actual"] = Hex.Format(actual);
        return node;
    }

    /// <summary>One <c>testsuite</c> per suite, one <c>testcase</c> per case, with <c>failure</c> and <c>skipped</c> elements.</summary>
    public static string JUnit(string project, IReadOnlyList<CaseResult> results)
    {
        var suites = results.GroupBy(r => r.Case.Suite).Select(suite => new XElement("testsuite",
            Counts(suite.Key, suite.ToList()),
            suite.Select(r => new XElement("testcase",
                new XAttribute("name", r.Case.Name),
                new XAttribute("classname", $"{project}.{r.Case.Suite}"),
                new XAttribute("time", Seconds(r.Duration)),
                r.Case.Source?.File is { } file ? new XAttribute("file", file) : null,
                r.Case.Source is { Line: > 0 } source ? new XAttribute("line", source.Line) : null,
                r.Skipped ? new XElement("skipped", new XAttribute("message", r.Case.Skip ?? "")) : null,
                r.Failed
                    ? new XElement("failure",
                        new XAttribute("message", r.Summary),
                        new XAttribute("type", r.Failures[0].Kind.ToString()),
                        string.Join("\n", r.Failures.Select(f => f.ToString())))
                    : null))));
        var root = new XElement("testsuites", Counts(project, results), suites);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + "\n" + root;
    }

    private static object[] Counts(string name, IReadOnlyCollection<CaseResult> results) =>
    [
        new XAttribute("name", name),
        new XAttribute("tests", results.Count),
        new XAttribute("failures", results.Count(r => r.Failed)),
        new XAttribute("errors", 0),
        new XAttribute("skipped", results.Count(r => r.Skipped)),
        new XAttribute("time", Seconds(TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks)))),
    ];

    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
