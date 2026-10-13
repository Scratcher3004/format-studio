using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Importers;

public sealed class ImportResult
{
    public List<TypeDef> Definitions { get; } = [];
    public Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);
    public DiagnosticBag Diagnostics { get; } = [];
    public string Report { get; set; } = "";
    /// <summary>The file the report was written to, if any.</summary>
    public string? ReportFile { get; set; }
    public List<string> Written { get; } = [];
    public List<string> Deleted { get; } = [];
    public int Unchanged { get; set; }
}

/// <summary>
/// A setting of an importer that the command line (<c>--cli-name value</c>) and the web UI let the user change for one
/// run. <see cref="Key"/> is the key in the project's <c>import.&lt;importer&gt;</c> settings.
/// </summary>
public sealed record ImportOption(string Key, string CliName, string Label, string? Help = null, bool IsPath = true, bool Multiple = false);

/// <summary>
/// Converts some external description of a format into DSL definitions in the project. The settings come from the
/// project's <c>import.&lt;Name&gt;</c> section, with <see cref="Options"/> overridden by the caller.
/// </summary>
public interface IImporter
{
    /// <summary>Name used in <c>import &lt;name&gt;</c> and in the project's <c>import</c> section.</summary>
    string Name { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>The start of the first line of every file the importer writes; only such files are replaced or deleted.</summary>
    string Marker { get; }
    IReadOnlyList<ImportOption> Options { get; }
    /// <summary>Runs the import. Relative paths in <paramref name="settings"/> are relative to the project folder.</summary>
    ImportResult Run(FormatProject project, JsonObject settings, bool write);
}

/// <summary>The importers known to the host. The command line and the web UI register theirs at startup.</summary>
public static class ImporterRegistry
{
    private static readonly List<IImporter> Registered = [];
    private static readonly Lock Gate = new();

    public static IReadOnlyList<IImporter> All
    {
        get
        {
            lock (Gate) return Registered.ToList();
        }
    }

    public static void Register(IImporter importer)
    {
        lock (Gate)
        {
            if (Registered.Any(i => i.Name == importer.Name)) return;
            Registered.Add(importer);
        }
    }

    public static IImporter Find(string name) =>
        All.FirstOrDefault(i => i.Name == name)
        ?? throw new FormatDefinitionException($"Unknown importer '{name}' ({string.Join(", ", All.Select(i => i.Name))})");

    /// <summary>The project's settings for an importer (a copy, so callers can override options).</summary>
    public static JsonObject Settings(FormatProject project, string name) =>
        project.Settings.Import[name] is JsonObject settings ? (JsonObject)settings.DeepClone() : new JsonObject();

    /// <summary>Whether the file was written by a registered importer.</summary>
    public static bool IsImported(string file)
    {
        if (!File.Exists(file)) return false;
        var first = File.ReadLines(file).FirstOrDefault();
        return first != null && All.Any(i => first.StartsWith(i.Marker, StringComparison.Ordinal));
    }
}
