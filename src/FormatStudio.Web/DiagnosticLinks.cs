using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Web;

/// <summary>Where a diagnostic is fixed: a page link with a button label.</summary>
public sealed record DiagnosticLink(string Page, string Label, string Icon);

/// <summary>Finds the page to fix a diagnostic on, for the buttons of <c>DiagnosticList</c>.</summary>
public static class DiagnosticLinks
{
    /// <summary>
    /// The protocols page for findings about protocols, the Edit tab of the definition in the diagnostic's file, the
    /// test case in it, or null.
    /// </summary>
    /// <returns>A page relative to the project (see <see cref="ProjectContext.Link"/>).</returns>
    public static DiagnosticLink? For(FormatProject project, Diagnostic diagnostic)
    {
        if (diagnostic.Area == Diagnostic.ProtocolsArea)
            return new DiagnosticLink("protocols", "Protocols", MudBlazor.Icons.Material.Filled.Hub);
        if (diagnostic.Source?.File is not { } file) return null;

        if (project.Types.Values.FirstOrDefault(t => t.Source?.File == file) is { } definition)
            return new DiagnosticLink($"type/{Uri.EscapeDataString(definition.Name)}?tab=edit", "Edit", MudBlazor.Icons.Material.Filled.Edit);

        // A test file holds several cases: the last one starting at or before the diagnostic's line.
        var tests = project.Tests.Select((test, index) => (Test: test, Index: index)).Where(t => t.Test.Source?.File == file).ToList();
        if (tests.Count == 0) return null;
        var line = diagnostic.Source.Line;
        var match = tests.LastOrDefault(t => line == 0 || t.Test.Source!.Line <= line);
        if (match.Test == null) match = tests[0];
        return new DiagnosticLink($"test/{match.Index}", "Edit", MudBlazor.Icons.Material.Filled.Edit);
    }
}
