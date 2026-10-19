using System.Text.Json.Nodes;
using FormatStudio.Dsl;
using FormatStudio.Model;
using YamlDotNet.RepresentationModel;

namespace FormatStudio.Projects;

/// <summary>
/// Reads and edits sample files. A file holds a list of samples, a mapping with a <c>samples</c> list, or a single
/// sample. Edits keep the shape of the file and the keys of the samples they don't touch, but not comments.
/// </summary>
public static class SampleFiles
{
    /// <summary>The sample entries of a parsed sample file.</summary>
    public static IReadOnlyList<JsonObject> Entries(JsonNode? root) => root switch
    {
        JsonArray array => array.OfType<JsonObject>().ToList(),
        JsonObject obj when obj["samples"] is JsonArray array => array.OfType<JsonObject>().ToList(),
        JsonObject obj => [obj],
        _ => [],
    };

    /// <summary>The keys a sample entry may have.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["sample", "name", "type", "value", "hex"];

    public static string? NameOf(JsonObject entry) => entry["sample"]?.ToString() ?? entry["name"]?.ToString();

    /// <summary>
    /// The samples of a sample file, each with the line it starts on. Entries that aren't samples are reported to
    /// <paramref name="diagnostics"/> and skipped, so that one broken entry doesn't hide the others.
    /// </summary>
    /// <param name="file">The file as shown in diagnostics, relative to the project.</param>
    public static IReadOnlyList<SampleDef> Load(string text, string file, DiagnosticBag diagnostics)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count == 0) return [];

        var root = stream.Documents[0].RootNode;
        IReadOnlyList<YamlNode> entries = root switch
        {
            YamlSequenceNode sequence => sequence.Children.ToList(),
            YamlMappingNode mapping when mapping.Children.TryGetValue(new YamlScalarNode("samples"), out var list) && list is YamlSequenceNode sequence
                => sequence.Children.ToList(),
            YamlMappingNode mapping => [mapping],
            _ => [],
        };

        var samples = new List<SampleDef>();
        var defaultName = Path.GetFileNameWithoutExtension(file);
        // Indices count mappings only, as Entries does, since edits address samples by index.
        var index = 0;
        foreach (var node in entries)
        {
            var source = new SourceLocation(file, (int)node.Start.Line, (int)node.Start.Column);
            if (node is not YamlMappingNode || YamlValues.ToJson(node) is not JsonObject entry)
            {
                diagnostics.Error("A sample must be a mapping", source);
                continue;
            }

            try
            {
                samples.Add(Read(entry, defaultName, source, index));
            }
            catch (FormatDefinitionException e)
            {
                diagnostics.Error(e.Message, source);
            }

            index++;
        }

        return samples;
    }

    public static SampleDef Read(JsonObject entry, string defaultName, SourceLocation source, int index) => new()
    {
        Name = NameOf(entry) ?? defaultName,
        Type = entry["type"]?.ToString() ?? throw new FormatDefinitionException("A sample needs a 'type'", source),
        Value = entry["value"]?.DeepClone(),
        Hex = entry["hex"]?.ToString(),
        Source = source,
        Index = index,
        UnknownKeys = entry.Select(pair => pair.Key).Where(key => !Keys.Contains(key)).ToList(),
    };

    /// <summary>
    /// The text of a sample file with the sample at <paramref name="index"/> replaced by <paramref name="sample"/>, removed
    /// (no sample) or, without an index, with the sample added at the end. Null when no sample is left.
    /// </summary>
    public static string? Replace(string? text, int? index, SampleDef? sample)
    {
        var root = string.IsNullOrWhiteSpace(text) ? null : YamlValues.ParseYaml(text);
        var entries = Entries(root).ToList();
        if (index is { } at && (at < 0 || at >= entries.Count)) throw new ArgumentOutOfRangeException(nameof(index));

        if (sample == null && index is { } removed) entries.RemoveAt(removed);
        else if (sample != null && index is { } replaced) entries[replaced] = Entry(sample, entries[replaced]);
        else if (sample != null) entries.Add(Entry(sample, null));
        if (entries.Count == 0) return null;

        var list = new JsonArray(entries.Select(e => (JsonNode)(e.Parent == null ? e : e.DeepClone())).ToArray());
        JsonNode result = root switch
        {
            JsonArray => list,
            JsonObject obj when obj["samples"] is JsonArray => WithSamples(obj, list),
            JsonObject when list.Count == 1 => list[0]!.DeepClone(),
            _ => new JsonObject { ["samples"] = list },
        };
        return YamlEmitter.Write(result);
    }

    private static JsonObject WithSamples(JsonObject root, JsonArray samples)
    {
        var copy = (JsonObject)root.DeepClone();
        copy["samples"] = samples;
        return copy;
    }

    /// <summary>The entry for a sample, keeping the key order and any other keys of the entry it replaces.</summary>
    private static JsonObject Entry(SampleDef sample, JsonObject? original)
    {
        var entry = original == null ? new JsonObject() : (JsonObject)original.DeepClone();
        entry[entry.ContainsKey("name") && !entry.ContainsKey("sample") ? "name" : "sample"] = sample.Name;
        entry["type"] = sample.Type;
        Set(entry, "value", sample.Value?.DeepClone());
        Set(entry, "hex", sample.Hex);
        return entry;
    }

    private static void Set(JsonObject entry, string key, JsonNode? value)
    {
        if (value == null) entry.Remove(key);
        else entry[key] = value;
    }
}
