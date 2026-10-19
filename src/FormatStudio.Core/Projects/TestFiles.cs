using System.Text.Json;
using System.Text.Json.Nodes;
using FormatStudio.Dsl;
using FormatStudio.Model;
using YamlDotNet.RepresentationModel;

namespace FormatStudio.Projects;

/// <summary>
/// Reads and edits test files. A file holds a list of cases, a suite (a mapping with a <c>cases</c> list and defaults
/// for them), or a single case. <c>samples</c> and <c>sample</c> are the old names of <c>cases</c> and <c>name</c>.
/// Edits keep the shape of the file and the keys of the cases they don't touch, but not comments.
/// </summary>
public static class TestFiles
{
    /// <summary>The keys of a case.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["name", "sample", "type", "value", "hex", "file", "expect", "packet", "fuzz", "skip", "tags"];

    /// <summary>The keys of a suite besides its cases: defaults for the cases of the file.</summary>
    public static IReadOnlyList<string> SuiteKeys { get; } = ["suite", "type", "packet", "tags", "skip"];

    /// <summary>The list of cases of a suite: its <c>cases</c>, or its <c>samples</c> in older files.</summary>
    private static string? ListKey(JsonObject root) =>
        root["cases"] is JsonArray ? "cases" : root["samples"] is JsonArray ? "samples" : null;

    /// <summary>The case entries of a parsed test file.</summary>
    public static IReadOnlyList<JsonObject> Entries(JsonNode? root) => root switch
    {
        JsonArray array => array.OfType<JsonObject>().ToList(),
        JsonObject obj when ListKey(obj) is { } key => obj[key]!.AsArray().OfType<JsonObject>().ToList(),
        JsonObject obj => [obj],
        _ => [],
    };

    public static string? NameOf(JsonObject entry) => entry["name"]?.ToString() ?? entry["sample"]?.ToString();

    /// <summary>
    /// The cases of a test file, each with the line it starts on. Problems are reported to <paramref name="diagnostics"/>;
    /// an entry that isn't a case is skipped, so that one broken entry doesn't hide the others.
    /// </summary>
    /// <param name="file">The file as shown in diagnostics, relative to the project.</param>
    public static IReadOnlyList<TestCaseDef> Load(string text, string file, DiagnosticBag diagnostics)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count == 0) return [];

        var root = stream.Documents[0].RootNode;
        var suite = new JsonObject();
        IReadOnlyList<YamlNode> entries = [];
        switch (root)
        {
            case YamlSequenceNode sequence:
                entries = sequence.Children.ToList();
                break;
            case YamlMappingNode mapping when YamlValues.ToJson(mapping) is JsonObject obj && ListKey(obj) is { } key:
                entries = ((YamlSequenceNode)mapping.Children[new YamlScalarNode(key)]).Children.ToList();
                suite = obj;
                foreach (var unknown in obj.Select(p => p.Key).Where(k => k != key && !SuiteKeys.Contains(k)))
                    diagnostics.Warning($"Unknown suite key '{unknown}' ({string.Join(", ", SuiteKeys)}, cases)", Location(file, mapping));
                break;
            case YamlMappingNode mapping:
                entries = [mapping];
                break;
        }

        var cases = new List<TestCaseDef>();
        var suiteName = suite["suite"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);
        // Indices count mappings only, as Entries does, since edits address cases by index.
        var index = 0;
        foreach (var node in entries)
        {
            var source = Location(file, node);
            if (node is not YamlMappingNode map || YamlValues.ToJson(node) is not JsonObject entry)
            {
                diagnostics.Error("A test case must be a mapping", source);
                continue;
            }

            // Hex as written: a plain 0102 would otherwise read as the number 102.
            if (map.Children.TryGetValue(new YamlScalarNode("hex"), out var hex) && hex is YamlScalarNode { Value: { } digits })
                entry["hex"] = digits;

            try
            {
                cases.Add(Read(entry, suite, suiteName, source, index));
            }
            catch (FormatDefinitionException e)
            {
                diagnostics.Error(e.Message, source);
            }

            index++;
        }

        return cases;
    }

    private static SourceLocation Location(string file, YamlNode node) => new(file, (int)node.Start.Line, (int)node.Start.Column);

    /// <summary>Reads a case of a file whose suite (the file's mapping, or empty) is <paramref name="suite"/>.</summary>
    public static TestCaseDef Read(JsonObject entry, JsonObject suite, string suiteName, SourceLocation source, int index)
    {
        var name = NameOf(entry) ?? $"{suiteName}-{index + 1}";
        var unknown = entry.Select(pair => pair.Key).Where(key => !Keys.Contains(key)).ToList();
        var expect = entry["expect"] switch
        {
            null => null,
            JsonObject map => ReadExpectation(map, name, source, unknown),
            _ => throw new FormatDefinitionException($"Test {name}: 'expect' must be a mapping", source),
        };
        var packet = entry.ContainsKey("packet")
            ? ReadPacket(entry["packet"], name, source, unknown)
            : suite.ContainsKey("packet") ? ReadPacket(suite["packet"], name, source, []) : null;
        var fuzz = entry.ContainsKey("fuzz") ? ReadFuzz(entry["fuzz"], name, source, unknown) : null;
        var type = (entry["type"] ?? suite["type"])?.ToString();
        if (type == null && packet == null && fuzz == null)
            throw new FormatDefinitionException($"Test {name} needs a 'type' (or its suite one)", source);

        return new TestCaseDef
        {
            Name = name,
            Type = type,
            Value = entry["value"]?.DeepClone(),
            Hex = entry["hex"]?.ToString(),
            File = entry["file"]?.ToString(),
            Expect = expect,
            Packet = packet,
            Fuzz = fuzz,
            Skip = SkipReason(entry["skip"]) ?? SkipReason(suite["skip"]),
            Tags = Strings(suite["tags"]).Concat(Strings(entry["tags"])).Distinct().ToList(),
            Suite = suiteName,
            Source = source,
            Index = index,
            UnknownKeys = unknown,
            Entry = (JsonObject)entry.DeepClone(),
        };
    }

    private static TestExpectation ReadExpectation(JsonObject map, string name, SourceLocation source, List<string> unknown)
    {
        unknown.AddRange(map.Select(pair => pair.Key).Where(key => !TestExpectation.Keys.Contains(key)).Select(key => "expect." + key));
        var expectation = new TestExpectation
        {
            HasValue = map.ContainsKey("value"),
            Value = map["value"]?.DeepClone(),
            Error = map["error"]?.ToString(),
            Path = map["path"]?.ToString(),
            Warnings = map.ContainsKey("warnings") ? Strings(map["warnings"]) : null,
        };

        expectation.Match = map["match"]?.ToString() switch
        {
            null or "exact" => MatchMode.Exact,
            "subset" => MatchMode.Subset,
            var other => throw new FormatDefinitionException($"Test {name}: unknown match '{other}' (exact, subset)", source),
        };
        expectation.Fails = map["fails"]?.ToString() switch
        {
            null => null,
            "encode" => ExpectedFailure.Encode,
            "decode" => ExpectedFailure.Decode,
            var other => throw new FormatDefinitionException($"Test {name}: unknown fails '{other}' (encode, decode)", source),
        };
        if (map["consumed"] is { } consumed)
        {
            expectation.Consumed = consumed is JsonValue v && v.TryGetValue<long>(out var count) && count is >= 0 and <= int.MaxValue
                ? (int)count
                : throw new FormatDefinitionException($"Test {name}: 'consumed' must be a number of bytes", source);
        }

        return expectation;
    }

    /// <summary>A <c>packet</c> key: a mapping, or just the header type, e.g. <c>packet: varint32</c>.</summary>
    private static PacketFrame? ReadPacket(JsonNode? node, string name, SourceLocation source, List<string> unknown)
    {
        var map = node switch
        {
            null => null,
            JsonObject obj => obj,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => new JsonObject { ["header"] = value.ToString() },
            _ => throw new FormatDefinitionException($"Test {name}: 'packet' must be a header type or a mapping", source),
        };
        if (map == null) return null;
        unknown.AddRange(map.Select(pair => pair.Key).Where(key => !PacketFrame.Keys.Contains(key)).Select(key => "packet." + key));

        var header = map["header"]?.ToString() ?? throw new FormatDefinitionException($"Test {name}: 'packet' needs a 'header', e.g. varint32", source);
        if (!Primitives.TryParse(header, out var primitive) || !Primitives.IsInteger(primitive.Kind))
            throw new FormatDefinitionException($"Test {name}: the packet header '{header}' is not an integer type", source);
        var frame = new PacketFrame { Header = primitive, Protocol = map["protocol"]?.ToString(), Group = map["group"]?.ToString() };
        if (map["id-mask"] is { } mask)
        {
            var text = mask.ToString();
            try
            {
                frame.IdMask = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(text[2..], 16) : ulong.Parse(text);
            }
            catch (Exception e) when (e is System.FormatException or OverflowException)
            {
                throw new FormatDefinitionException($"Test {name}: 'id-mask' must be a number, e.g. 0x3ff", source);
            }
        }

        return frame;
    }

    /// <summary>A <c>fuzz</c> key: <c>true</c> or a mapping with iterations and a seed.</summary>
    private static FuzzOptions? ReadFuzz(JsonNode? node, string name, SourceLocation source, List<string> unknown)
    {
        switch (node)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.True:
                return new FuzzOptions();
            case JsonValue value when value.GetValueKind() == JsonValueKind.False:
                return null;
            case JsonObject map:
                unknown.AddRange(map.Select(pair => pair.Key).Where(key => !FuzzOptions.Keys.Contains(key)).Select(key => "fuzz." + key));
                var fuzz = new FuzzOptions();
                if (map["iterations"] is { } iterations) fuzz.Iterations = Number(iterations, "iterations", 1);
                if (map["seed"] is { } seed) fuzz.Seed = Number(seed, "seed", int.MinValue);
                return fuzz;
            default:
                throw new FormatDefinitionException($"Test {name}: 'fuzz' must be true or a mapping (iterations, seed)", source);
        }

        int Number(JsonNode value, string key, int min) =>
            value is JsonValue v && v.TryGetValue<long>(out var number) && number >= min && number <= int.MaxValue
                ? (int)number
                : throw new FormatDefinitionException($"Test {name}: 'fuzz.{key}' must be a whole number{(min == 1 ? " above 0" : "")}", source);
    }

    private static string? SkipReason(JsonNode? skip) => skip switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.False => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.True => "skipped",
        _ => skip.ToString(),
    };

    private static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(item => item?.ToString()).OfType<string>().ToList(),
        null => [],
        _ => [node.ToString()],
    };

    /// <summary>
    /// The text of a test file with the case at <paramref name="index"/> replaced by <paramref name="test"/>, removed (no
    /// test) or, without an index, with the case added at the end. Null when no case is left.
    /// </summary>
    public static string? Replace(string? text, int? index, TestCaseDef? test)
    {
        var root = string.IsNullOrWhiteSpace(text) ? null : YamlValues.ParseYaml(text);
        var entries = Entries(root).ToList();
        if (index is { } at && (at < 0 || at >= entries.Count)) throw new ArgumentOutOfRangeException(nameof(index));

        var suite = root is JsonObject obj && ListKey(obj) != null ? obj : null;
        if (test == null && index is { } removed) entries.RemoveAt(removed);
        else if (test != null && index is { } replaced) entries[replaced] = Entry(test, entries[replaced], suite);
        else if (test != null) entries.Add(Entry(test, test.Entry, suite));
        if (entries.Count == 0) return null;

        var list = new JsonArray(entries.Select(e => (JsonNode)(e.Parent == null ? e : e.DeepClone())).ToArray());
        JsonNode result = root switch
        {
            JsonArray => list,
            JsonObject when suite != null => WithCases(suite, ListKey(suite)!, list),
            JsonObject when list.Count == 1 => list[0]!.DeepClone(),
            _ => new JsonObject { ["cases"] = list },
        };
        return YamlEmitter.Write(result);
    }

    private static JsonObject WithCases(JsonObject root, string key, JsonArray cases)
    {
        var copy = (JsonObject)root.DeepClone();
        copy[key] = cases;
        return copy;
    }

    /// <summary>
    /// The entry for a case, keeping the key order and any other keys of the entry it replaces (or of the entry it was read
    /// from, when it moves to another file). The type is left to the suite when the suite has the same one.
    /// </summary>
    private static JsonObject Entry(TestCaseDef test, JsonObject? original, JsonObject? suite)
    {
        var entry = original == null ? new JsonObject() : (JsonObject)original.DeepClone();
        entry[entry.ContainsKey("sample") && !entry.ContainsKey("name") ? "sample" : "name"] = test.Name;
        if (test.Type == null) entry.Remove("type");
        else if (entry.ContainsKey("type") || suite?["type"]?.ToString() != test.Type) entry["type"] = test.Type;
        Set(entry, "value", test.Value?.DeepClone());
        Set(entry, "hex", test.Hex);
        Set(entry, "file", test.File);
        return entry;
    }

    private static void Set(JsonObject entry, string key, JsonNode? value)
    {
        if (value == null) entry.Remove(key);
        else entry[key] = value;
    }
}
