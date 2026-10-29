using System.Text.Json.Nodes;
using FormatStudio.Minecraft.Netty;
using FormatStudio.Natives;
using FormatStudio.Simulation;

namespace FormatStudio.Minecraft;

/// <summary>
/// <c>nbt</c>: a root NBT tag in one of the <see cref="NbtFlavor"/>s, as SNBT text (or <c>{name, snbt}</c> for a named
/// root). Generated code keeps the tag's raw bytes, except that of the <c>netty</c> generator, which has Minecraft's tags.
/// </summary>
public sealed class NbtNative : INativeType
{
    public static NbtNative Instance { get; } = new();

    // The reading and writing code is a file of the generated code (Files/), not part of the runtime.
    private static readonly NativeCodegen CSharp = new("byte[]",
        "global::{{namespace}}.NbtCodec.Read{{flavor:pascal}}({{pass-reader}})",
        "global::{{namespace}}.NbtCodec.Write{{flavor:pascal}}({{pass-writer}}, {{value}})",
        [new NativeFile("NbtCodec.cs", MinecraftFiles.Text("NbtCodec.cs.txt"))]);
    private static readonly NativeCodegen CSharpStream = new("byte[]",
        "global::{{namespace}}.NbtStreamCodec.Read{{flavor:pascal}}({{pass-reader}})",
        "global::{{namespace}}.NbtStreamCodec.Write{{flavor:pascal}}({{pass-writer}}, {{value}})",
        [new NativeFile("NbtStreamCodec.cs", MinecraftFiles.Text("NbtStreamCodec.cs.txt"))]);
    private static readonly NativeCodegen Java = new("byte[]",
        "{{namespace}}.NbtCodec.read{{flavor:pascal}}({{reader}})",
        "{{namespace}}.NbtCodec.write{{flavor:pascal}}({{writer}}, {{value}})",
        [new NativeFile("NbtCodec.java", MinecraftFiles.Text("NbtCodec.java.txt"))]);
    // Minecraft's own tags; the netty generator only allows the network flavor, the one Minecraft's codecs read.
    private static readonly NativeCodegen NettyCode = new("net.minecraft.nbt.Tag",
        "{{namespace}}.NettyNbt.read{{flavor:pascal}}({{reader}})",
        "{{namespace}}.NettyNbt.write{{flavor:pascal}}({{writer}}, {{value}})",
        [new NativeFile("NbtCodec.java", MinecraftFiles.Text("NbtCodec.java.txt")), new NativeFile("NettyNbt.java", MinecraftFiles.Text("NettyNbt.java.txt"))])
    {
        Codec = "net.minecraft.network.codec.ByteBufCodecs.TAG",
    };

    private NbtNative()
    {
    }

    public string Name => "nbt";

    public string Description => "An NBT tag (Minecraft's named binary tag), shown and edited as SNBT text.";

    public IReadOnlyList<NativeParameter> Parameters { get; } =
    [
        new("flavor", NativeParameterKind.Choice, "Byte order, root name and integer encoding.",
            [.. Enum.GetValues<NbtFlavor>().Select(Nbt.FlavorName)],
            Missing: "NBT without a flavor: set defaults.nbt in the project or write nbt(<flavor>)"),
    ];

    public bool Nullable => true;

    // A lone TAG_End.
    public int MinSize(NativeArguments args) => 1;

    private static NbtFlavor Flavor(NativeArguments args) => Nbt.ParseFlavor(args["flavor"]);

    public JsonNode? Read(INativeReader reader, NativeArguments args) => Nbt.ReadValue(reader, Flavor(args));

    public void Write(INativeWriter writer, NativeArguments args, JsonNode? value) => Nbt.WriteValue(writer, Flavor(args), value);

    public JsonNode DefaultValue(NativeArguments args) => JsonValue.Create("{}");

    public JsonNode RandomValue(NativeArguments args, Random random, int depth) =>
        JsonValue.Create($"{{name:\"n{random.Next(100)}\",value:{random.Next(-1000, 1000)},list:[{random.Next(10)}b,{random.Next(10)}b]}}");

    public NativeCodegen? Codegen(string generator) => generator switch
    {
        "csharp" => CSharp,
        "csharp-stream" => CSharpStream,
        "java-stream" => Java,
        NettyGenerator.GeneratorName => NettyCode,
        _ => null,
    };
}
