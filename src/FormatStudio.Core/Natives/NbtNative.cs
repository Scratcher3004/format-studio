using System.Text.Json.Nodes;
using FormatStudio.Simulation;

namespace FormatStudio.Natives;

/// <summary>
/// <c>nbt</c>: a root NBT tag in one of the <see cref="NbtFlavor"/>s, as SNBT text (or <c>{name, snbt}</c> for a named
/// root). Generated code keeps the tag's raw bytes.
/// </summary>
public sealed class NbtNative : INativeType
{
    public static NbtNative Instance { get; } = new();

    private static readonly NativeCodegen CSharp = new("byte[]", "{{reader}}.ReadNbt{{flavor:pascal}}()", "{{writer}}.WriteNbt{{flavor:pascal}}({{value}})");
    private static readonly NativeCodegen Java = new("byte[]", "{{reader}}.readNbt{{flavor:pascal}}()", "{{writer}}.writeNbt{{flavor:pascal}}({{value}})");

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
        "csharp" or "csharp-stream" => CSharp,
        "java-stream" => Java,
        _ => null,
    };
}
