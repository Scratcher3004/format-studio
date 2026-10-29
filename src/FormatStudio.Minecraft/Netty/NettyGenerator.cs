using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Codegen.Java;
using FormatStudio.Projects;

namespace FormatStudio.Minecraft.Netty;

/// <summary>How the netty generator builds a struct's <c>StreamCodec</c>.</summary>
public enum NettyStrategy
{
    /// <summary><c>StreamCodec.composite</c> where the struct allows it, else <see cref="Manual"/>.</summary>
    Auto,
    /// <summary>Always <c>StreamCodec.composite</c>; a struct that can't be one is an error.</summary>
    Composite,
    /// <summary>A codec over the generated read and write code (<c>NettyFormat.codec</c>).</summary>
    Manual,
}

/// <summary>The options of the netty generator besides those of java-stream.</summary>
/// <param name="Buffer">The buffer type of the generated codecs, e.g. <c>net.minecraft.network.RegistryFriendlyByteBuf</c>.</param>
public sealed record NettyOptions(NettyStrategy Strategy, string Buffer);

/// <summary>
/// Generates Java records for Minecraft's networking: the java-stream code (as records, with lists), plus a
/// <c>StreamCodec</c> per type and, for packets with text ids, the <c>CustomPacketPayload</c> members. Structs use
/// <c>StreamCodec.composite</c> where every field has a codec of Minecraft's (<c>ByteBufCodecs</c>, the codecs of other
/// generated types and of bindings), otherwise a codec over the generated read and write code. A generator of the
/// <c>minecraft</c> library.
/// </summary>
public sealed class NettyGenerator : ICodeGenerator
{
    public const string GeneratorName = "netty";

    public static NettyGenerator Instance { get; } = new();

    private NettyGenerator()
    {
    }

    public string Name => GeneratorName;

    public string Title => "Java (Minecraft StreamCodecs)";

    public string Language => "java";

    public string Description =>
        "Java records with a Minecraft StreamCodec each (StreamCodec.composite where the fields allow it), and the CustomPacketPayload " +
        "members of packets with text ids. Built on java-stream; needs Java 16 and Minecraft's classes.";

    public string DefaultOutput => "generated/netty";

    public string Marker => JavaGenerator.FileMarker;

    public IReadOnlyList<CodegenOption> Options { get; } =
    [
        .. JavaOptions.Describe().Where(o => o.Key is not ("records" or "collections")),
        CodegenOption.Choice("strategy", "Codecs", ["auto", "composite", "manual"],
            "auto: StreamCodec.composite where a struct allows it, else a codec over the generated read and write code. composite: " +
            "a struct that can't be composed is an error. manual: always the generated read and write code."),
        CodegenOption.Text("buffer", "Buffer type", "net.minecraft.network.RegistryFriendlyByteBuf", "The buffer type of the generated StreamCodecs.", true),
    ];

    public GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped)
    {
        var java = JavaOptions.From(options);
        // Records with lists, the types StreamCodec.composite and ByteBufCodecs.list build.
        java.Records = true;
        java.Collections = JavaCollections.List;
        java.Exclude = skipped;
        var read = new OptionReader(options, Options);
        var netty = new NettyOptions(Enum.Parse<NettyStrategy>(read.Choice("strategy"), true), read.Text("buffer"));
        return new NettyEmitter(project, java, netty).Run();
    }
}
