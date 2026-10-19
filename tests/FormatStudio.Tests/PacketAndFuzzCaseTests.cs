using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>Test cases whose bytes start with a packet id, and cases that round-trip random values.</summary>
public class PacketAndFuzzCaseTests
{
    /// <summary>
    /// Packets <c>A {a: u8}</c> (id 1) and <c>B {b: u16}</c> (id 2) in protocol X, and <c>C {c: u8}</c> (also id 1) in
    /// protocol Y, a struct <c>Point</c>, and the given test file.
    /// </summary>
    private static FormatProject Project(string tests)
    {
        var directory = TestPaths.NewTempDirectory();
        ProjectTemplate.Create(directory, "test");
        File.AppendAllText(Path.Combine(directory, "test" + ProjectSettings.Extension),
            "protocols:\n  - name: X\n    groups: [x]\n  - name: Y\n    groups: [y]\n");
        File.WriteAllText(Path.Combine(directory, "packets", "A.yaml"), "packet: A\nid: 1\ngroups: [x]\nfields:\n  - a: u8\n");
        File.WriteAllText(Path.Combine(directory, "packets", "B.yaml"), "packet: B\nid: 2\ngroups: [x]\nfields:\n  - b: u16\n");
        File.WriteAllText(Path.Combine(directory, "packets", "C.yaml"), "packet: C\nid: 1\ngroups: [y]\nfields:\n  - c: u8\n");
        File.WriteAllText(Path.Combine(directory, "types", "Point.yaml"), "struct: Point\nfields:\n  - x: u8\n  - y: u8\n");
        File.WriteAllText(Path.Combine(directory, "tests", "tests.yaml"), tests);
        return FormatProject.Load(directory);
    }

    private static Dictionary<string, CaseResult> Results(FormatProject project) => TestRunner.Run(project).ToDictionary(r => r.Case.Name);

    private static void AssertNoErrors(FormatProject project) =>
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity == Severity.Error);

    [Fact]
    public void PacketCasesPickThePacketByItsId()
    {
        var project = Project("""
            suite: framed
            packet: {header: u8, protocol: X}
            cases:
              - {name: a, hex: 01 07, expect: {value: {a: 7}}}
              - {name: b, type: B, value: {b: 258}, hex: 02 0201}
              - {name: wrong-packet, type: B, hex: 01 07, expect: {error: "are A (id 1), not B"}}
              - {name: unknown-id, hex: 09 00, expect: {error: no packet has id 9 in X}}
              - {name: wrong-hex, type: A, value: {a: 7}, hex: 02 07}
              - {name: trailing, hex: 01 07 ff, expect: {consumed: 2}}
              - {name: other-protocol, packet: {header: u8, protocol: Y}, hex: 01 07, expect: {value: {c: 7}}}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        foreach (var name in new[] { "a", "b", "wrong-packet", "unknown-id", "trailing", "other-protocol" })
            Assert.True(results[name].Passed, $"{name}: {results[name].Summary}");

        // The id is part of the bytes, so a mismatch is at the offset in the whole packet.
        var mismatch = Assert.Single(results["wrong-hex"].Failures);
        Assert.Equal(CaseFailureKind.Mismatch, mismatch.Kind);
        Assert.Equal(0, mismatch.Offset);
        Assert.Equal("0107", Hex.Format(mismatch.Actual));
        Assert.Equal("packet of X", project.Tests[0].Subject);
    }

    [Fact]
    public void PacketHeadersCanBeShortAndTruncated()
    {
        var project = Project("""
            - {name: short, type: A, packet: varint32, value: {a: 1}, hex: 01 01}
            - {name: truncated, packet: u16, hex: "00"}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        Assert.True(results["short"].Passed, results["short"].Summary);
        var truncated = Assert.Single(results["truncated"].Failures);
        Assert.Equal(CaseFailureKind.RoundTrip, truncated.Kind);
        Assert.StartsWith("Packet id:", truncated.Message);
    }

    [Fact]
    public void FuzzCasesRoundTripRandomValues()
    {
        var project = Project("""
            suite: fuzz
            cases:
              - {name: point, type: Point, fuzz: {iterations: 30}}
              - {name: all-packets, fuzz: true}
              - {name: protocol-x, packet: {header: u8, protocol: X}, fuzz: {iterations: 10}}
              # Without a protocol, A and C share id 1, so the id of C reads as A.
              - {name: shared-ids, packet: u8, fuzz: {iterations: 5, seed: 3}}
            """);
        AssertNoErrors(project);
        var results = Results(project);

        Assert.True(results["point"].Passed, results["point"].Summary);
        Assert.True(results["all-packets"].Passed, results["all-packets"].Summary);
        Assert.True(results["protocol-x"].Passed, results["protocol-x"].Summary);

        var shared = Assert.Single(results["shared-ids"].Failures);
        Assert.Equal(CaseFailureKind.Fuzz, shared.Kind);
        Assert.Equal("C: 5 of 5 round trips fail, e.g. The bytes are A (id 1), not C", shared.Message);
        Assert.NotNull(shared.Value);

        // The same seed gives the same values in every run, also in other processes.
        var again = Assert.Single(Results(project)["shared-ids"].Failures);
        Assert.Equal(shared.Value!.ToJsonString(), again.Value!.ToJsonString());
        Assert.Equal(-260436673, Simulator.FuzzSeed(1, "A", 0));
    }

    [Fact]
    public void ProblemsWithPacketAndFuzzCasesAreReportedWhenTheProjectLoads()
    {
        var project = Project("""
            - {name: not-a-packet, type: Point, packet: u8, hex: 01 02}
            - {name: no-protocol, packet: {header: u8, protocol: Z}, hex: "01"}
            - {name: other-protocol, type: C, packet: {header: u8, protocol: X}, hex: 01 07}
            - {name: float-header, packet: f32, hex: "01"}
            - {name: value-without-type, packet: u8, value: {a: 1}}
            - {name: fuzz-with-hex, type: Point, fuzz: true, hex: 01 02}
            - {name: bad-iterations, type: Point, fuzz: {iterations: 0}}
            - {name: unknown-keys, packet: {header: u8, colour: red}, fuzz: {iterations: 1, rounds: 2}, hex: "01"}
            """);
        var messages = project.Diagnostics.Select(d => d.Message).ToList();

        Assert.Contains("Test not-a-packet: Point is not a packet, so its bytes can't start with a packet id", messages);
        Assert.Contains("Test no-protocol: there is no protocol 'Z'", messages);
        Assert.Contains("Test other-protocol: C is not one of the packets the id is looked up among (protocol X)", messages);
        Assert.Contains("Test float-header: the packet header 'f32' is not an integer type", messages);
        Assert.Contains("Test value-without-type: a packet case with a 'value' needs a 'type', the packet to encode it as", messages);
        Assert.Contains("Test fuzz-with-hex: a fuzz case makes up its values, so it has no 'value', 'hex', 'file' or 'expect'", messages);
        Assert.Contains("Test bad-iterations: 'fuzz.iterations' must be a whole number above 0", messages);
        Assert.Contains("Test unknown-keys: unknown key 'packet.colour' (header, protocol, group, id-mask)", messages);
        Assert.Contains("Test unknown-keys: unknown key 'fuzz.rounds' (iterations, seed)", messages);
    }

    [Fact]
    public void EditsKeepThePacketAndFuzzKeys()
    {
        const string tests = """
            - {name: framed, packet: {header: u8, protocol: X}, hex: 01 07}
            - {name: fuzz, fuzz: {iterations: 3}}
            """;
        var project = Project(tests);
        var framed = project.Tests[0];
        var edited = new TestCaseDef { Name = "framed2", Hex = "02 0100", Packet = framed.Packet, Entry = framed.Entry };

        var replaced = YamlValues.ParseYaml(TestFiles.Replace(tests, 0, edited)!)!;
        Assert.Equal("X", replaced[0]!["packet"]!["protocol"]!.GetValue<string>());
        Assert.Null(replaced[0]!["type"]);
        Assert.Equal(3, replaced[1]!["fuzz"]!["iterations"]!.GetValue<long>());
    }
}
