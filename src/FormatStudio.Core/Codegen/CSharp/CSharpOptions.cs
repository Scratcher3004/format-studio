using System.Text.Json.Nodes;

namespace FormatStudio.Codegen.CSharp;

/// <summary>How generated C# reads and writes bytes.</summary>
public enum CSharpIo
{
    /// <summary><c>ref struct</c> reader over a <c>ReadOnlySpan&lt;byte&gt;</c>, writer over an <c>IBufferWriter&lt;byte&gt;</c>.</summary>
    Span,
    /// <summary>Reader and writer classes over a <c>System.IO.Stream</c>.</summary>
    Stream,
}

/// <summary>
/// Options of the C# generators. The runtime names (reader, writer, interfaces, exception) default to
/// FormatStudio.Runtime for the I/O model; each can be overridden to use another runtime with the same methods.
/// </summary>
public sealed class CSharpOptions
{
    public CSharpIo Io { get; set; } = CSharpIo.Span;
    public string Namespace { get; set; } = "Generated";
    public string RuntimeNamespace { get; set; } = "FormatStudio.Runtime";
    public string ReaderType { get; set; } = "FormatReader";
    public string WriterType { get; set; } = "FormatWriter";
    public string SerializableInterface { get; set; } = "IFormatSerializable";
    public string PacketInterface { get; set; } = "IFormatPacket";
    public string VisitorInterface { get; set; } = "IFormatPacketVisitor";
    public string TextPacketInterface { get; set; } = "IFormatTextPacket";
    public string TextVisitorInterface { get; set; } = "IFormatTextPacketVisitor";
    public string ExceptionType { get; set; } = "FormatDataException";
    /// <summary>Name of the static class with the packet id dispatch.</summary>
    public string Registry { get; set; } = "Packets";
    /// <summary>Types that are referenced but not generated (from the definitions' <c>skipCodegen</c>).</summary>
    public IReadOnlySet<string> Exclude { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Check min/max of numeric fields when reading.</summary>
    public bool ValidateRanges { get; set; }
    /// <summary>Reject enum values that are not members (unless the enum allows unknown values or is a flags enum) when reading.</summary>
    public bool ValidateEnums { get; set; }
    /// <summary>Reject invalid UTF-8 and, where <c>check</c> is strict, padding or fill that wouldn't re-encode the same.</summary>
    public bool ValidateStrings { get; set; } = true;

    public static IReadOnlyList<CodegenOption> Describe(CSharpIo io)
    {
        var stream = io == CSharpIo.Stream;
        return
        [
            CodegenOption.Text("namespace", "Namespace", "Generated", "The namespace of the generated types."),
            CodegenOption.Text("registry", "Registry class", "Packets", "The static class with the packet id dispatch."),
                CodegenOption.Bool("validateRanges", "Validate ranges", false, "Check min/max of numeric fields when reading."),
            CodegenOption.Bool("validateEnums", "Validate enums", false, "Reject values that are not enum members when reading (except flags enums and enums that allow unknown values)."),
            CodegenOption.Bool("validateStrings", "Validate strings", true, "Reject invalid UTF-8, and padding or fill that wouldn't re-encode the same where check is strict."),
            CodegenOption.Text("runtimeNamespace", "Runtime namespace", "FormatStudio.Runtime", "The namespace of the runtime the generated code calls.", true),
            CodegenOption.Text("reader", "Reader type", stream ? "FormatStreamReader" : "FormatReader",
                stream ? "Reader class over a Stream." : "ref struct reader over a ReadOnlySpan<byte>.", true),
            CodegenOption.Text("writer", "Writer type", stream ? "FormatStreamWriter" : "FormatWriter",
                stream ? "Writer class over a Stream." : "ref struct writer over an IBufferWriter<byte>.", true),
            CodegenOption.Text("serializableInterface", "Serializable interface", stream ? "IFormatStreamSerializable" : "IFormatSerializable",
                "Implemented by every generated type: static abstract Read, Write.", true),
            CodegenOption.Text("packetInterface", "Packet interface", stream ? "IFormatStreamPacket" : "IFormatPacket",
                "Implemented by packets: adds static abstract uint PacketId.", true),
            CodegenOption.Text("visitorInterface", "Visitor interface", stream ? "IFormatStreamPacketVisitor" : "IFormatPacketVisitor",
                "Receives packets from the registry's dispatch.", true),
            CodegenOption.Text("textPacketInterface", "Text packet interface", stream ? "IFormatStreamTextPacket" : "IFormatTextPacket",
                "Implemented by packets with text ids: adds static abstract string PacketId.", true),
            CodegenOption.Text("textVisitorInterface", "Text visitor interface", stream ? "IFormatStreamTextPacketVisitor" : "IFormatTextPacketVisitor",
                "Receives packets with text ids from the registry's dispatch.", true),
            CodegenOption.Text("exception", "Exception type", "FormatDataException", "Thrown for malformed data.", true),
        ];
    }

    public static CSharpOptions From(JsonObject options, CSharpIo io = CSharpIo.Span)
    {
        var read = new OptionReader(options, Describe(io));
        return new CSharpOptions
        {
            Io = io,
            Namespace = read.Text("namespace"),
            Registry = read.Text("registry"),
            ValidateRanges = read.Bool("validateRanges"),
            ValidateEnums = read.Bool("validateEnums"),
            ValidateStrings = read.Bool("validateStrings"),
            RuntimeNamespace = read.Text("runtimeNamespace"),
            ReaderType = read.Text("reader"),
            WriterType = read.Text("writer"),
            SerializableInterface = read.Text("serializableInterface"),
            PacketInterface = read.Text("packetInterface"),
            VisitorInterface = read.Text("visitorInterface"),
            TextPacketInterface = read.Text("textPacketInterface"),
            TextVisitorInterface = read.Text("textVisitorInterface"),
            ExceptionType = read.Text("exception"),
        };
    }
}
