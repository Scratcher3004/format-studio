using System.IO.Compression;
using System.Text.Json.Nodes;
using FormatStudio.Projects;

namespace FormatStudio.Importers.Mojang;

/// <summary>
/// The JSON files of a Mojang/bedrock-protocol-docs snapshot, read from a release zip or a directory. Keys are the
/// file names without <c>.json</c>, case-sensitive (the docs contain e.g. both <c>item_descriptor</c> and
/// <c>Item_Descriptor</c>).
/// </summary>
public sealed class SchemaSource
{
    public required string Origin { get; init; }
    public Dictionary<string, JsonObject> Documents { get; } = new(StringComparer.Ordinal);

    public string? MinecraftVersion => Documents.Values.Select(d => d["x-minecraft-version"]?.ToString()).FirstOrDefault(v => v != null);
    public int? ProtocolVersion => Documents.Values.Select(d => (int?)d["x-protocol-version"]?.GetValue<int>()).FirstOrDefault(v => v != null);

    /// <summary>Loads from a zip (any <c>json/</c> folder inside) or a directory containing the JSON files or a <c>json/</c> folder.</summary>
    public static SchemaSource Load(string path, FileSandbox? sandbox = null)
    {
        sandbox ??= FileSandbox.Unrestricted;
        path = sandbox.Check(path);
        var source = new SchemaSource { Origin = path };
        if (File.Exists(path))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                var parts = entry.FullName.Split('/');
                if (parts.Length < 2 || parts[^2] != "json" || !entry.Name.EndsWith(".json", StringComparison.Ordinal)) continue;
                using var stream = entry.Open();
                source.Add(entry.Name, JsonNode.Parse(stream));
            }
        }
        else if (Directory.Exists(path))
        {
            var directory = Directory.Exists(Path.Combine(path, "json")) ? Path.Combine(path, "json") : path;
            foreach (var file in sandbox.EnumerateFiles(directory, "*.json"))
            {
                using var stream = File.OpenRead(file);
                source.Add(Path.GetFileName(file), JsonNode.Parse(stream));
            }
        }
        else
        {
            throw new FileNotFoundException($"Schema not found: {path}");
        }

        if (source.Documents.Count == 0) throw new InvalidDataException($"No schema JSON files in {path}");
        return source;
    }

    private void Add(string fileName, JsonNode? node)
    {
        if (node is JsonObject obj) Documents[fileName[..^".json".Length]] = obj;
    }

    /// <summary>The document key a <c>$ref</c> like <c>./Foo.json</c> points to.</summary>
    public static string RefKey(string reference)
    {
        var name = reference.StartsWith("./", StringComparison.Ordinal) ? reference[2..] : reference.TrimStart('/');
        return name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name;
    }
}
