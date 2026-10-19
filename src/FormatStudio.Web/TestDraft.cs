using System.Text.Json.Nodes;
using FormatStudio.Projects;

namespace FormatStudio.Web;

/// <summary>What a page offers to save as a test case (see the <c>SaveAsTest</c> component).</summary>
/// <param name="Name">A name for the case; a number is added when the file has it already.</param>
/// <param name="Type">The type expression; null for a packet case whose id picks the packet.</param>
/// <param name="Packet">For bytes that start with a packet id.</param>
/// <param name="Decoded">The value the bytes decode to, to expect.</param>
/// <param name="Consumed">How many of the bytes the type takes up, when not all of them.</param>
public sealed record TestDraft(string Name, string? Type, JsonNode? Value, byte[]? Bytes, PacketFrame? Packet = null, JsonNode? Decoded = null, int? Consumed = null)
{
    public string Subject => Type ?? (Packet?.Protocol is { } protocol ? $"packet of {protocol}" : "packet");

    /// <summary>A case name from a type: <c>BlockUpdate</c> gives <c>block-update</c>, <c>Slot[]</c> <c>slot</c>.</summary>
    public static string NameFor(string? type)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(type ?? "", "(?<=[a-z0-9])(?=[A-Z])", "-");
        var name = System.Text.RegularExpressions.Regex.Replace(words.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return name.Length > 0 ? name : "case";
    }
}
