using FormatStudio.Dsl;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;
using FormatStudio.Testing;

namespace FormatStudio.Tests;

/// <summary>The projects in the repository: valid, test cases, random round trips.</summary>
public class ProjectTests
{
    public static TheoryData<string> Projects => [TestPaths.JavaSample];

    [Theory]
    [MemberData(nameof(Projects))]
    public void IsValid(string path)
    {
        var project = FormatProject.Load(path);
        Assert.DoesNotContain(project.Diagnostics, d => d.Severity != Severity.Info);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void SamplesEncodeAsExpected(string path)
    {
        var project = FormatProject.Load(path);
        Assert.NotEmpty(project.Tests);
        foreach (var result in TestRunner.Run(project))
            Assert.True(result.Passed, $"{result.Case.Name}: {string.Join("\n", result.Failures.Select(f => f.Message))}");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void RandomValuesRoundTrip(string path)
    {
        var project = FormatProject.Load(path);
        var failures = Simulator.Fuzz(project, project.Types.Values.OfType<StructDef>(), seed: 42, iterations: 5)
            .Where(r => !r.Success)
            .Select(r => $"{r.Type}: {string.Join("; ", r.Diagnostics.Where(d => d.Severity == Severity.Error))}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void DefaultValuesEncode(string path)
    {
        var project = FormatProject.Load(path);
        var codec = new Codec(project);
        var defaults = new DefaultValues(project);
        var failures = project.Types.Values.OfType<StructDef>()
            .Select(s => (s.Name, Result: codec.Encode(new NamedRef(s.Name), defaults.Generate(new NamedRef(s.Name)))))
            .Where(r => !r.Result.Success)
            .Select(r => $"{r.Name}: {string.Join("; ", r.Result.Diagnostics.Where(d => d.Severity == Severity.Error))}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Theory]
    [InlineData("enum: NewEnum\ntype: u8\nvalues: {a: 0}\n", "types/enums/NewEnum.yaml", "enums/NewEnum.yaml")]
    [InlineData("alias: NewAlias\ntype: u8\n", "types/aliases/NewAlias.yaml", "aliases/NewAlias.yaml")]
    [InlineData("struct: NewStruct\nfields:\n  - x: u8\n", "types/NewStruct.yaml", "types/NewStruct.yaml")]
    [InlineData("packet: NewPacket\nid: 0\nfields:\n  - x: u8\n", "packets/NewPacket.yaml", "packets/NewPacket.yaml")]
    public void NewDefinitionsGoToAFolderOfTheirKindThatIsLoaded(string yaml, string inJavaSample, string withDefaultSources)
    {
        var definition = DslReader.Read(yaml, "new.yaml", new DiagnosticBag())!;
        definition.Source = null;

        var java = FormatProject.Load(TestPaths.JavaSample);
        Assert.Equal(inJavaSample, java.Relative(java.FileFor(definition)));
        Assert.True(java.IsSource(java.FileFor(definition)));

        var plain = FormatProject.InMemory([]);
        Assert.Equal(withDefaultSources, plain.Relative(plain.FileFor(definition)));
    }

    [Fact]
    public void ProtocolsAreReadAndWritten()
    {
        var settings = ProjectSettings.Parse("""
            name: p
            protocols:
              - name: LoginServerbound
                groups: [login, serverbound]
                doc: Before the login finishes.
              - name: Status
                groups: status
            """, "p.fstudio.yaml");
        Assert.Equal(["LoginServerbound", "Status"], settings.Protocols.Select(p => p.Name));
        Assert.Equal(["login", "serverbound"], settings.Protocols[0].Groups);
        Assert.Equal("Before the login finishes.", settings.Protocols[0].Doc);
        Assert.Equal(["status"], settings.Protocols[1].Groups);

        var again = ProjectSettings.Parse(settings.ToYaml(), "p.fstudio.yaml");
        Assert.Equal(settings.Protocols.Select(p => (p.Name, string.Join(",", p.Groups), p.Doc)),
            again.Protocols.Select(p => (p.Name, string.Join(",", p.Groups), p.Doc)));
    }

    [Fact]
    public void PacketIdsOnlyClashWithinAProtocol()
    {
        static StructDef Packet(string name, long id, params string[] groups)
        {
            var packet = new StructDef { Name = name, IsPacket = true, Id = id };
            packet.Groups.AddRange(groups);
            return packet;
        }

        var settings = new ProjectSettings
        {
            Protocols =
            [
                new ProtocolDef { Name = "LoginServerbound", Groups = ["login", "serverbound"] },
                new ProtocolDef { Name = "PlayServerbound", Groups = ["play", "serverbound"] },
                new ProtocolDef { Name = "Empty", Groups = ["config"] },
                new ProtocolDef { Name = "Hello", Groups = ["login"] },
            ],
        };
        var project = FormatProject.InMemory([
            Packet("Hello", 0, "login", "serverbound"),
            Packet("Chat", 0, "play", "serverbound"),
            Packet("Move", 0, "play", "serverbound", "movement"),
            Packet("Ping", 0, "status"),
        ], settings);
        Validation.Validator.Validate(project);

        Assert.Equal(["Chat", "Move"], project.PacketsOf(settings.Protocols[1]).Select(p => p.Name).Order());
        var messages = project.Diagnostics.Select(d => $"{d.Severity}: {d.Message}").ToList();
        Assert.Contains("Error: Packets Chat, Move share id 0 in protocol PlayServerbound", messages);
        Assert.DoesNotContain(messages, m => m.Contains("Hello, Chat") || m.Contains("in protocol LoginServerbound"));
        Assert.Contains(messages, m => m.StartsWith("Warning: Protocol Empty has no packets"));
        Assert.Contains("Warning: Packet Ping is in no protocol, so no registry dispatches it", messages);
        Assert.Contains("Error: Protocol Hello has the name of a definition", messages);
    }

    [Fact]
    public void SavingProtocolsOnlyChangesTheProtocolsSection()
    {
        var directory = TestPaths.NewTempDirectory();
        var file = Path.Combine(directory, "p.fstudio.yaml");
        const string before = "name: p\n# One id space per state\nprotocols:\n  - name: Old\n    groups: [a]\ncodegen:\n  - name: cs\n    generator: csharp\n    output: out\n";
        File.WriteAllText(file, before);
        var project = FormatProject.Load(directory);

        project.SaveProtocols([new ProtocolDef { Name = "LoginServerbound", Groups = ["login", "serverbound"], Doc = "Login." }]);
        Assert.Equal("name: p\n# One id space per state\nprotocols:\n  - name: LoginServerbound\n    groups:\n      - login\n      - serverbound\n    doc: Login.\n" +
                     "codegen:\n  - name: cs\n    generator: csharp\n    output: out\n", File.ReadAllText(file));
        Assert.Equal("LoginServerbound", project.Settings.Protocols.Single().Name);
        Assert.Equal("cs", FormatProject.Load(directory).Settings.Codegen.Single().Name);

        project.SaveProtocols([]);
        Assert.Equal("name: p\n# One id space per state\ncodegen:\n  - name: cs\n    generator: csharp\n    output: out\n", File.ReadAllText(file));

        var invalid = new ProtocolDef { Name = "a b", Groups = ["x"] };
        Assert.Throws<FormatDefinitionException>(() => project.SaveProtocols([invalid]));
        var duplicate = new ProtocolDef { Name = "A" };
        Assert.Throws<FormatDefinitionException>(() => project.SaveProtocols([duplicate, duplicate]));
        Assert.Throws<FormatDefinitionException>(() => project.SaveProtocols([new ProtocolDef { Name = "A", Groups = ["has space"] }]));
    }
}
