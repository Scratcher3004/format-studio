using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Testing;

/// <summary>What became of one case in <see cref="TestSnapshots.UpdateAll"/>.</summary>
/// <param name="Updated">The keys that were written, e.g. <c>hex</c>; empty when none were.</param>
/// <param name="Reason">Why the case still fails, when it does.</param>
public sealed record SnapshotOutcome(TestCaseDef Case, IReadOnlyList<string> Updated, string? Reason);

/// <summary>
/// Accepting what a failed case actually gives as what it expects: the bytes its value encodes to, the value its bytes
/// decode to, its warnings and length. Failures to encode, decode or round-trip are left to be fixed by hand, as are
/// bytes from a file.
/// </summary>
public static class TestSnapshots
{
    private static readonly HashSet<CaseFailureKind> Acceptable =
        [CaseFailureKind.Mismatch, CaseFailureKind.Value, CaseFailureKind.Consumed, CaseFailureKind.Warnings, CaseFailureKind.Snapshot];

    /// <summary>Rounds of <see cref="UpdateAll"/>: an update can show further differences, e.g. a new length, a new value.</summary>
    private const int Rounds = 3;

    /// <summary>The update after which the case gives what it expects, or why there is none.</summary>
    public static (TestUpdate? Update, string? Reason) Accept(FormatProject project, CaseResult result)
    {
        var test = result.Case;
        if (!result.Failed) return (null, "it passes");
        if (result.Failures.FirstOrDefault(f => !Acceptable.Contains(f.Kind)) is { } other) return (null, other.ToString());

        string? hex = null;
        if (result.Failures.Any(f => f.Kind == CaseFailureKind.Mismatch) || test.HexAuto)
        {
            if (test.File != null) return (null, $"its bytes come from {test.File}; change the file or the value");
            if (test.Expect?.Consumed != null && !test.HexAuto) return (null, "its bytes go on after the value ('consumed'); change them by hand");
            if (result.Bytes == null) return (null, "the value doesn't encode");
            hex = FormatHex(project, test, result.Bytes);
        }

        var expect = test.Expect;
        var setsValue = expect != null && (expect.ValueAuto || result.Failures.Any(f => f.Kind == CaseFailureKind.Value));
        var value = !setsValue ? null
            : expect!.Match == MatchMode.Subset && !expect.ValueAuto ? Project(expect.Value, result.Decoded)
            : result.Decoded?.DeepClone();
        var update = new TestUpdate
        {
            Hex = hex,
            SetsValue = setsValue,
            Value = value,
            Warnings = result.Failures.Any(f => f.Kind == CaseFailureKind.Warnings) ? result.Warnings.Select(w => w.Message).Distinct().ToList() : null,
            Consumed = result.Failures.Any(f => f.Kind == CaseFailureKind.Consumed) ? result.Consumed : null,
        };
        return update.Keys.Count > 0 ? (update, null) : (null, "nothing to accept");
    }

    /// <summary>
    /// Runs the cases and writes what the failed ones give into their files, then runs them again, as long as that
    /// changes something (a few rounds at most).
    /// </summary>
    /// <param name="reload">Loads the project again after files were written.</param>
    public static IReadOnlyList<SnapshotOutcome> UpdateAll(FormatProject project, Func<FormatProject> reload, TestFilter? filter,
        out FormatProject updated)
    {
        var outcomes = new Dictionary<(string?, int), SnapshotOutcome>();
        HashSet<(string?, int)>? pending = null;
        for (var round = 0; round < Rounds; round++)
        {
            var written = new HashSet<(string?, int)>();
            foreach (var result in TestRunner.Run(project, filter).Where(r => r.Failed))
            {
                var key = (result.Case.Source?.File, result.Case.Index);
                if (pending != null && !pending.Contains(key)) continue;
                var keys = outcomes.TryGetValue(key, out var earlier) ? earlier.Updated : [];
                var (update, reason) = Accept(project, result);
                if (update != null)
                {
                    try
                    {
                        project.UpdateTest(result.Case, update);
                        written.Add(key);
                        keys = keys.Union(update.Keys).ToList();
                        reason = null;
                    }
                    catch (Exception e) when (e is FormatDefinitionException or IOException or UnauthorizedAccessException or ArgumentException or YamlDotNet.Core.YamlException)
                    {
                        reason = "not written: " + e.Message;
                    }
                }

                outcomes[key] = new SnapshotOutcome(result.Case, keys, reason);
            }

            if (written.Count == 0) break;
            project = reload();
            pending = written;
        }

        updated = project;
        return outcomes.Values.ToList();
    }

    /// <summary>The fields of a subset that the actual value has, with the actual values.</summary>
    private static JsonNode? Project(JsonNode? expected, JsonNode? actual) => (expected, actual) switch
    {
        (JsonObject e, JsonObject a) => new JsonObject(e.Where(p => a.ContainsKey(p.Key))
            .Select(p => KeyValuePair.Create(p.Key, Project(p.Value, a[p.Key])))),
        (JsonArray e, JsonArray a) when e.Count == a.Count => new JsonArray(e.Zip(a, Project).ToArray()),
        _ => actual?.DeepClone(),
    };

    /// <summary>The bytes in hex with a space after a packet id and between top-level fields, as cases are written by hand.</summary>
    public static string FormatHex(FormatProject project, TestCaseDef test, byte[] bytes)
    {
        var cuts = new SortedSet<int>();
        try
        {
            var decoded = TestRunner.Subject.For(new Codec(project), test).Decode(bytes);
            cuts.Add(decoded.HeaderLength);
            foreach (var child in decoded.Root?.Children ?? [])
            {
                cuts.Add(decoded.HeaderLength + child.Offset);
                cuts.Add(decoded.HeaderLength + child.Offset + child.Length);
            }
        }
        catch (Exception e) when (e is System.FormatException or FormatDefinitionException)
        {
            return Hex.Format(bytes);
        }

        var parts = new List<string>();
        var start = 0;
        foreach (var cut in cuts.Where(c => c > 0 && c < bytes.Length).Append(bytes.Length))
        {
            if (cut <= start) continue;
            parts.Add(Hex.Format(bytes.AsSpan(start, cut - start)));
            start = cut;
        }

        return string.Join(" ", parts);
    }
}
