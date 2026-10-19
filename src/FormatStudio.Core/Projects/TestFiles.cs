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
    public static IReadOnlyList<string> Keys { get; } = ["name", "sample", "type", "value", "hex", "file", "expect", "packet", "fuzz", "skip", "skip-targets", "tags"];

    /// <summary>The keys of a suite besides its cases: defaults for the cases of the file.</summary>
    public static IReadOnlyList<string> SuiteKeys { get; } = ["suite", "type", "packet", "tags", "skip", "skip-targets"];

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
            // The case's names override the suite's, also with false.
            SkipTargets = SkipTargets(suite["skip-targets"]).Concat(SkipTargets(entry["skip-targets"]))
                .GroupBy(p => p.Key).Select(g => g.Last()).Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!),
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
            ValueAuto = map["value"] is JsonValue placeholder && placeholder.GetValueKind() == JsonValueKind.String
                                                              && placeholder.GetValue<string>() == TestCaseDef.Auto,
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

    /// <summary>A list of names, or a mapping of names to why ("" when no reason is given, null for <c>false</c>).</summary>
    private static IEnumerable<KeyValuePair<string, string?>> SkipTargets(JsonNode? node) => node is JsonObject map
        ? map.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.True || p.Value == null ? "" : SkipReason(p.Value)))
        : Strings(node).Select(name => new KeyValuePair<string, string?>(name, ""));

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

    /// <summary>Lines that a new expected value may take up before it is written as a block instead of on one line.</summary>
    private const int MaxWidth = 100;

    /// <summary>
    /// The text of a test file with the hex and expectations of the case at <paramref name="index"/> replaced where they
    /// stand. Unlike <see cref="Replace"/> it keeps comments and the layout of the rest of the file. Only keys the case
    /// has are replaced.
    /// </summary>
    public static string Update(string text, int index, TestUpdate update)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var entry = (stream.Documents.Count > 0 ? EntryNodes(stream.Documents[0].RootNode) : []).ElementAtOrDefault(index)
                    ?? throw new ArgumentOutOfRangeException(nameof(index));
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var edits = new List<(int Start, int End, string Text)>();
        // Hex as written, never quoted: it is read from the raw scalar.
        if (update.Hex != null) edits.Add(Edit(text, entry, "hex", null, update.Hex, newline));
        if (update.SetsValue || update.Warnings != null || update.Consumed != null)
        {
            var expect = Find(entry, "expect")?.Value as YamlMappingNode
                         ?? throw new FormatDefinitionException($"Test {NameOf(entry)} has no 'expect' to update");
            if (update.SetsValue) edits.Add(Edit(text, expect, "value", update.Value, null, newline));
            if (update.Warnings != null)
                edits.Add(Edit(text, expect, "warnings", new JsonArray(update.Warnings.Select(w => (JsonNode)w).ToArray()), null, newline));
            if (update.Consumed is { } consumed) edits.Add(Edit(text, expect, "consumed", consumed, null, newline));
        }

        foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start))
            text = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
        return text;
    }

    private static string? NameOf(YamlMappingNode entry) => (Find(entry, "name") ?? Find(entry, "sample"))?.Value is YamlScalarNode { Value: { } name } ? name : null;

    /// <summary>The case mappings of a parsed test file, counted as <see cref="Entries"/> counts them.</summary>
    private static IEnumerable<YamlMappingNode> EntryNodes(YamlNode root) => root switch
    {
        YamlSequenceNode sequence => sequence.Children.OfType<YamlMappingNode>(),
        YamlMappingNode mapping when (Find(mapping, "cases") ?? Find(mapping, "samples"))?.Value is YamlSequenceNode cases =>
            cases.Children.OfType<YamlMappingNode>(),
        YamlMappingNode mapping => [mapping],
        _ => [],
    };

    private static KeyValuePair<YamlNode, YamlNode>? Find(YamlMappingNode mapping, string key) =>
        mapping.Children.Where(p => p.Key is YamlScalarNode { Value: var k } && k == key).Select(p => (KeyValuePair<YamlNode, YamlNode>?)p).FirstOrDefault();

    /// <summary>
    /// Replaces the value of <paramref name="key"/>: with <paramref name="raw"/> text, else with the value on one line,
    /// or as a block when the old value was one or the line would get too long.
    /// </summary>
    private static (int, int, string) Edit(string text, YamlMappingNode parent, string key, JsonNode? value, string? raw, string newline)
    {
        var (keyNode, valueNode) = Find(parent, key) ?? throw new FormatDefinitionException($"The case has no '{key}' to update");
        var start = (int)valueNode.Start.Index;
        var end = End(text, valueNode);
        var flow = raw ?? YamlEmitter.WriteFlow(value);
        var wasBlock = valueNode is YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: > 0 }
            or YamlSequenceNode { Style: not YamlDotNet.Core.Events.SequenceStyle.Flow, Children.Count: > 0 };
        var keyColumn = (int)keyNode.Start.Column - 1;
        var canBlock = raw == null && parent.Style != YamlDotNet.Core.Events.MappingStyle.Flow && value is JsonObject { Count: > 0 } or JsonArray { Count: > 0 };
        if (!canBlock || !wasBlock && keyColumn + key.Length + 2 + flow.Length <= MaxWidth) return (start, end, flow);

        var lines = YamlEmitter.Write(value).TrimEnd('\n').Split('\n');
        if (wasBlock)
        {
            // The block starts where the old one did, already indented.
            var indent = new string(' ', (int)valueNode.Start.Column - 1);
            return (start, end, lines[0] + string.Concat(lines.Skip(1).Select(line => newline + indent + line)));
        }

        // The old value was on the key's line: the block goes below the key.
        start = (int)keyNode.End.Index;
        while (start < text.Length && text[start] != ':') start++;
        var below = new string(' ', keyColumn + 2);
        return (start + 1, end, string.Concat(lines.Select(line => newline + below + line)));
    }

    /// <summary>
    /// Where a value ends: after its last scalar for a block collection, so that comments after it stay, and after the
    /// closing bracket for a flow collection. (A collection node's own end is where it starts.)
    /// </summary>
    private static int End(string text, YamlNode node)
    {
        var flow = node is YamlMappingNode { Style: YamlDotNet.Core.Events.MappingStyle.Flow } or YamlSequenceNode { Style: YamlDotNet.Core.Events.SequenceStyle.Flow };
        YamlNode? lastChild = node switch
        {
            YamlMappingNode { Children.Count: > 0 } mapping => mapping.Children.Last().Value,
            YamlSequenceNode { Children.Count: > 0 } sequence => sequence.Children.Last(),
            _ => null,
        };
        var last = lastChild != null ? End(text, lastChild) : (int)node.End.Index;
        if (flow)
        {
            if (lastChild == null) last = (int)node.Start.Index + 1;
            while (last < text.Length && text[last] is not ('}' or ']')) last++;
            return Math.Min(last + 1, text.Length);
        }

        while (last > (int)node.Start.Index && char.IsWhiteSpace(text[last - 1])) last--;
        return last;
    }

    private static void Set(JsonObject entry, string key, JsonNode? value)
    {
        if (value == null) entry.Remove(key);
        else entry[key] = value;
    }
}
