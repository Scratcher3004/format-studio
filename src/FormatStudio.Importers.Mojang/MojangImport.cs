using System.Text.Json.Nodes;
using FormatStudio.Projects;

namespace FormatStudio.Importers.Mojang;

/// <summary>The <c>mojang</c> importer for the command line and the web UI; see <see cref="MojangImporter"/>.</summary>
public sealed class MojangImport : IImporter
{
    public string Name => "mojang";
    public string Title => "Mojang bedrock-protocol-docs";

    public string Description =>
        "Converts the JSON schema of a Mojang/bedrock-protocol-docs release into one definition file per type in the " +
        "import folder, plus import-report.md.";

    public string Marker => MojangImporter.HeaderMarker;

    public IReadOnlyList<ImportOption> Options { get; } =
    [
        new("schema", "schema", "Schema (zip or folder)"),
        new("enumValuesFrom", "enum-values-from", "Enum values from (optional, zip or folder)",
            "A newer snapshot that has x-enum-binary-value. Without it, the values of the previous import are kept.", Multiple: true),
        new("output", "output", "Output folder"),
    ];

    public ImportResult Run(FormatProject project, JsonObject settings, bool write) =>
        MojangImporter.Run(MojangImportSettings.From(settings), project.Directory, write, project.Defaults, project.Sandbox);
}
