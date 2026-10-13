using System.Text.Json.Nodes;

namespace FormatStudio.Codegen.CSharp;

/// <summary>
/// Options of the C# generator. A profile names the runtime the generated code calls: the reader and writer
/// <c>ref struct</c>s, the interfaces and the exception type. The <c>default</c> profile uses FormatStudio.Runtime;
/// each name can be overridden in the options to use another runtime with the same methods.
/// </summary>
public sealed class CSharpOptions
{
    public string Namespace { get; set; } = "Generated";
    public string Profile { get; set; } = "default";
    public string RuntimeNamespace { get; set; } = "FormatStudio.Runtime";
    public string ReaderType { get; set; } = "FormatReader";
    public string WriterType { get; set; } = "FormatWriter";
    public string SerializableInterface { get; set; } = "IFormatSerializable";
    public string PacketInterface { get; set; } = "IFormatPacket";
    public string VisitorInterface { get; set; } = "IFormatPacketVisitor";
    public string ExceptionType { get; set; } = "FormatDataException";
    /// <summary>Name of the static class with the packet id dispatch.</summary>
    public string Registry { get; set; } = "Packets";
    /// <summary>Types that are written by hand. They are referenced but not generated.</summary>
    public HashSet<string> Exclude { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Check min/max of numeric fields when reading.</summary>
    public bool ValidateRanges { get; set; }
    /// <summary>Reject enum values that are not members (unless the enum allows unknown values) when reading.</summary>
    public bool ValidateEnums { get; set; }

    public static CSharpOptions From(JsonObject options)
    {
        var result = new CSharpOptions();
        string? Text(string key) => options[key]?.ToString();
        bool? Flag(string key) => options[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        result.Profile = Text("profile") ?? result.Profile;
        if (result.Profile != "default") throw new Model.FormatDefinitionException($"Unknown C# profile '{result.Profile}' (default)");
        result.Namespace = Text("namespace") ?? result.Namespace;
        result.RuntimeNamespace = Text("runtimeNamespace") ?? result.RuntimeNamespace;
        result.ReaderType = Text("reader") ?? result.ReaderType;
        result.WriterType = Text("writer") ?? result.WriterType;
        result.SerializableInterface = Text("serializableInterface") ?? result.SerializableInterface;
        result.PacketInterface = Text("packetInterface") ?? result.PacketInterface;
        result.VisitorInterface = Text("visitorInterface") ?? result.VisitorInterface;
        result.ExceptionType = Text("exception") ?? result.ExceptionType;
        result.Registry = Text("registry") ?? result.Registry;
        if (options["exclude"] is JsonArray exclude) result.Exclude = exclude.Select(e => e!.ToString()).ToHashSet(StringComparer.Ordinal);
        result.ValidateRanges = Flag("validateRanges") ?? false;
        result.ValidateEnums = Flag("validateEnums") ?? false;
        return result;
    }
}
