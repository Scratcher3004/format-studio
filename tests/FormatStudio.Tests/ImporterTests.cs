using System.Text.Json.Nodes;
using FormatStudio.Importers.Mojang;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Tests;

public class ImporterTests
{
    private static JsonObject Doc(string title, string body) =>
        JsonNode.Parse($$"""{"title": "{{title}}", "x-minecraft-version": "9.9.9", "x-protocol-version": 999, {{body}}}""")!.AsObject();

    /// <summary>A small schema with the shapes found in the real docs.</summary>
    private static string WriteSchema(string directory, bool withValues)
    {
        var json = Path.Combine(directory, "json");
        Directory.CreateDirectory(json);
        void Write(string name, JsonObject doc) => File.WriteAllText(Path.Combine(json, name + ".json"), doc.ToJsonString());

        Write("TestPacket", Doc("TestPacket", """
            "$ref": "./TestPacketPayload.json",
            "$metaProperties": {"[cereal:packet]": 5, "[cereal:packet_details]": "A test packet."}
            """));
        Write("TestPacketPayload", Doc("TestPacketPayload", """
            "type": "object",
            "properties": {
              "Died in Raid?": {"type": "boolean", "x-ordinal-index": 1},
              "Mode": {"$ref": "./GameMode.json", "x-underlying-type": "int32", "x-serialization-options": ["Compression", "Enum-as-Value"], "x-ordinal-index": 0},
              "Names": {"type": "array", "items": {"type": "string"}, "x-ordinal-index": 2},
              "Response": {"oneOf": [{"$ref": "./Case_-_A.json"}, {"$ref": "./Case_-_B.json"}], "x-control-value-type": "uint32", "x-ordinal-index": 3},
              "Extra": {"type": "string", "x-ordinal-index": 4},
              "Data": {"x-ordinal-index": 5},
              "Choice": {"oneOf": [{"type": "null"}, {"$ref": "./Vec.json"}], "x-control-value-type": "uint32", "x-ordinal-index": 6},
              "Fixed Count": {"type": "array", "items": {"type": "integer", "x-underlying-type": "uint16"}, "minItems": 2, "maxItems": 2, "x-ordinal-index": 7},
              "Raw": {"type": "array", "items": {"type": "integer", "x-underlying-type": "uint8"}, "x-serialization-options": ["No size compression"], "x-ordinal-index": 8},
              "Lookup": {"type": "object", "additionalProperties": {"type": "integer", "x-underlying-type": "int32"}, "x-ordinal-index": 9}
            },
            "required": ["Died in Raid?", "Mode", "Names", "Response", "Data", "Choice", "Fixed Count", "Raw", "Lookup"]
            """));
        var values = withValues ? """, "x-enum-binary-value": [0, 5]""" : "";
        Write("GameMode", Doc("GameMode", $$"""
            "type": "string", "enum": ["Survival", "Creative"], "x-underlying-type": "int32",
            "$enumDescription": {"Creative": "Flying allowed."}{{values}}
            """));
        Write("Kind", Doc("Kind", """
            "type": "string", "enum": ["A", "B"], "x-underlying-type": "uint8", "x-enum-binary-value": [1, 2]
            """));
        Write("Case_-_A", Doc("Case - A", """
            "type": "object",
            "properties": {"Kind": {"const": "a", "$ref": "./Kind.json", "x-underlying-type": "uint8", "x-ordinal-index": 0}},
            "required": ["Kind"]
            """));
        Write("Case_-_B", Doc("Case - B", """
            "type": "object",
            "properties": {
              "Kind": {"const": "b", "$ref": "./Kind.json", "x-underlying-type": "uint8", "x-ordinal-index": 0},
              "Value": {"type": "integer", "x-underlying-type": "int32", "x-serialization-options": ["Compression"], "x-ordinal-index": 1}
            },
            "required": ["Kind", "Value"]
            """));
        Write("Vec", Doc("Vec", """
            "type": "object",
            "properties": {"x": {"type": "number", "x-underlying-type": "float", "x-ordinal-index": 0}, "y": {"type": "number", "x-underlying-type": "float", "x-ordinal-index": 1}},
            "required": ["x", "y"]
            """));
        return directory;
    }

    [Fact]
    public void ImportsTheSchemaShapes()
    {
        var root = TestPaths.NewTempDirectory();
        var release = WriteSchema(Path.Combine(root, "release"), withValues: false);
        var snapshot = WriteSchema(Path.Combine(root, "snapshot"), withValues: true);
        var settings = new MojangImportSettings { Schema = release, EnumValuesFrom = [snapshot], Output = "imported" };

        var result = MojangImporter.Run(settings, root, write: true);
        Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics));
        var types = result.Definitions.ToDictionary(d => d.Name);

        var packet = Assert.IsType<StructDef>(types["TestPacket"]);
        Assert.Equal(5, packet.Id);
        Assert.Equal(["Mode", "DiedInRaid", "Names", "Response", "Extra", "Data", "Choice", "FixedCount", "Raw", "Lookup"],
            packet.Fields.Select(f => f.Name));
        Assert.Equal("Died in Raid?", packet.Fields[1].Title);
        Assert.Equal(new NamedRef("GameMode", new PrimitiveRef(PrimitiveKind.ZigZag32)), packet.Fields[0].Type);
        Assert.IsType<OptionalRef>(packet.Fields[4].Type);
        Assert.IsType<NativeRef>(packet.Fields[5].Type);
        Assert.Equal(LengthKind.Fixed, Assert.IsType<ArrayRef>(packet.Fields[7].Type).Length.Kind);
        Assert.Equal(new BytesRef(new LengthSpec(LengthKind.Prefix, new PrimitiveRef(PrimitiveKind.U32))), packet.Fields[8].Type);
        Assert.IsType<StringRef>(Assert.IsType<MapRef>(packet.Fields[9].Type).Key);

        var gameMode = Assert.IsType<EnumDef>(types["GameMode"]);
        Assert.Equal([0L, 5], gameMode.Members.Select(m => m.Value));
        Assert.Equal("Flying allowed.", gameMode.Members[1].Doc);

        // The const enum field of each case is the tag; case A has no other data.
        var response = Assert.IsType<VariantDef>(types[((NamedRef)packet.Fields[3].Type).Name]);
        Assert.Equal(new NamedRef("Kind"), response.Tag);
        Assert.Equal(["A", "B"], response.Cases.Select(c => c.TagMember));
        Assert.Null(response.Cases[0].Type);
        Assert.False(types.ContainsKey("CaseA"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.ZigZag32), Assert.IsType<AliasDef>(types["CaseB"]).Target);

        var choice = Assert.IsType<VariantDef>(types[((NamedRef)packet.Fields[6].Type).Name]);
        Assert.Equal(new PrimitiveRef(PrimitiveKind.VarUInt32), choice.Tag);
        Assert.Equal(["None", "Vec"], choice.Cases.Select(c => c.Name));

        // Written files form a valid project; a second import changes nothing and keeps hand-written files.
        File.WriteAllText(Path.Combine(root, "p.fstudio.yaml"), "name: p\ndefaults:\n  nbt: bedrock-network\nsources:\n  - imported/**/*.yaml\n");
        var handWritten = Path.Combine(root, "imported", "types", "HandWritten.yaml");
        File.WriteAllText(handWritten, "struct: HandWritten\nfields: []\n");
        var stale = Path.Combine(root, "imported", "types", "Stale.yaml");
        File.WriteAllText(stale, MojangImporter.HeaderMarker + " earlier\nstruct: Stale\nfields: []\n");

        var again = MojangImporter.Run(settings, root, write: true);
        Assert.Empty(again.Written);
        Assert.Equal(result.Definitions.Count, again.Unchanged);
        Assert.Equal([stale], again.Deleted);
        Assert.True(File.Exists(handWritten));
        Assert.True(File.Exists(Path.Combine(root, "imported", "import-report.md")));

        var project = FormatProject.Load(root);
        Assert.False(project.Diagnostics.HasErrors, string.Join("\n", project.Diagnostics));
        Assert.All(Simulator.Fuzz(project, project.Packets, 7, 20), r => Assert.True(r.Success, string.Join("\n", r.Diagnostics)));
    }

    [Fact]
    public void FallsBackToIndicesAndPreviousImport()
    {
        var root = TestPaths.NewTempDirectory();
        var release = WriteSchema(Path.Combine(root, "release"), withValues: false);
        var settings = new MojangImportSettings { Schema = release, Output = "imported" };

        var first = MojangImporter.Run(settings, root, write: true);
        var gameMode = (EnumDef)first.Definitions.Single(d => d.Name == "GameMode");
        Assert.Equal([0L, 1], gameMode.Members.Select(m => m.Value));
        Assert.Contains("member indices", first.Report);

        // Corrected by hand in the imported file (e.g. from a snapshot import): kept by the next import.
        var file = Path.Combine(root, "imported", "enums", "GameMode.yaml");
        File.WriteAllText(file, "enum: GameMode\ntype: i32\nvalues:\n  Survival: 0\n  Creative: 5\n");
        var second = MojangImporter.Run(settings, root, write: false);
        Assert.Equal([0L, 5], ((EnumDef)second.Definitions.Single(d => d.Name == "GameMode")).Members.Select(m => m.Value));
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("DiedInRaid", MojangImporter.Pascal("Died in Raid?"));
        Assert.Equal("MapInfoRequestPacketAnonClientPixelsProxy", MojangImporter.Pascal("(anonymous_namespace)__MapInfoRequestPacketAnon__ClientPixelsProxy"));
        Assert.Equal("StructuresNBT", MojangImporter.Pascal("Structure's NBT"));
        Assert.Equal("CTD", MojangImporter.Pascal("__[[CTD]]__"));
        Assert.Equal("_3dModel", MojangImporter.Pascal("3d model"));
        Assert.Equal("minecraft_stone", MojangImporter.MemberName("minecraft:stone"));
        Assert.Equal("SURVIVAL", MojangImporter.MemberName("SURVIVAL"));
    }
}
