using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Simulation;

namespace FormatStudio.Natives;

/// <summary>
/// A type the DSL can't describe, implemented in code: NBT, and in time the types of native libraries. Referenced as
/// <see cref="NativeRef"/> with arguments for its <see cref="Parameters"/>; everything that handles values (decoding,
/// encoding, default and random values, expressions, code generation) asks the type.
/// </summary>
public interface INativeType
{
    /// <summary>The name in type expressions, e.g. <c>nbt</c>.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>The arguments it takes, in order. A reference may leave out trailing ones (see <see cref="NativeTypes.Bind"/>).</summary>
    IReadOnlyList<NativeParameter> Parameters { get; }

    /// <summary>Whether a missing value (null) can be written, e.g. as an empty tag; required fields may then be left out.</summary>
    bool Nullable => false;

    /// <summary>The least number of bytes a value takes on the wire, to check counts before allocating.</summary>
    int MinSize(NativeArguments args) => 0;

    /// <summary>Reads a value at the reader's position and returns it as JSON.</summary>
    JsonNode? Read(INativeReader reader, NativeArguments args);

    /// <summary>Writes a value given as by <see cref="Read"/>.</summary>
    void Write(INativeWriter writer, NativeArguments args, JsonNode? value);

    /// <summary>The value of a new field or sample.</summary>
    JsonNode? DefaultValue(NativeArguments args);

    /// <summary>A random value, e.g. for round-trip tests; <paramref name="depth"/> is how deeply the value is nested.</summary>
    JsonNode? RandomValue(NativeArguments args, Random random, int depth);

    /// <summary>The value as expressions see it (<c>len(x)</c>, comparisons): by default the JSON text.</summary>
    object? ExpressionValue(JsonNode? value, NativeArguments args) => value?.ToJsonString();

    /// <summary>How generated code reads and writes values, for the generator of that name; null when it can't.</summary>
    NativeCodegen? Codegen(string generator);
}

public enum NativeParameterKind
{
    /// <summary>One of <see cref="NativeParameter.Choices"/>.</summary>
    Choice,
    /// <summary>Any text.</summary>
    Text,
    /// <summary>An integer.</summary>
    Integer,
    /// <summary>
    /// A type expression (<c>zlib(Chunk)</c>): the values of the native type contain values of that type, read and written
    /// with <see cref="INativeReader.Read"/>/<see cref="INativeWriter.Write"/> (or <c>Decode</c>/<c>Encode</c> for other
    /// bytes) and in generated code with the <c>{{T:read}}</c>/<c>{{T:write}}</c> functions.
    /// </summary>
    Type,
}

/// <param name="Default">The value when neither the reference nor the project's defaults give one; null makes it required.</param>
/// <param name="Missing">The error when a required argument is missing; null for a generic message.</param>
public sealed record NativeParameter(
    string Name,
    NativeParameterKind Kind,
    string Description,
    IReadOnlyList<string>? Choices = null,
    string? Default = null,
    string? Missing = null);

/// <summary>
/// The arguments of a native type reference, completed from the defaults: one value per parameter, in order (type
/// arguments as written), and the types of the <see cref="NativeParameterKind.Type"/> parameters.
/// </summary>
/// <param name="types">Per parameter, the type of a type parameter, else null; null for a type without type parameters.</param>
/// <param name="project">The project the reference is bound in, for the values of type arguments.</param>
public sealed class NativeArguments(IReadOnlyList<NativeParameter> parameters, IReadOnlyList<string> values,
    IReadOnlyList<TypeRef?>? types = null, FormatStudio.Projects.FormatProject? project = null)
{
    public IReadOnlyList<NativeParameter> Parameters => parameters;
    public IReadOnlyList<string> Values => values;

    public string this[string name] => values[IndexOf(name)];

    private int IndexOf(string name)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Name == name) return i;
        }

        throw new ArgumentException($"No parameter '{name}'", nameof(name));
    }

    public long Integer(string name) => long.Parse(this[name], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The type given for a <see cref="NativeParameterKind.Type"/> parameter.</summary>
    public TypeRef Type(string name)
    {
        var index = IndexOf(name);
        return types != null && index < types.Count && types[index] is { } type
            ? type
            : throw new ArgumentException($"'{name}' is not a type parameter", nameof(name));
    }

    private FormatStudio.Projects.FormatProject Project =>
        project ?? throw new InvalidOperationException("The arguments aren't bound in a project");

    /// <summary>The default value of the type given for a type parameter (see <see cref="INativeType.DefaultValue"/>).</summary>
    public JsonNode? DefaultValue(string name) => new DefaultValues(Project).Generate(Type(name));

    /// <summary>A random value of the type given for a type parameter; <paramref name="depth"/> is that of the native value.</summary>
    public JsonNode? RandomValue(string name, Random random, int depth) => RandomValues.Nested(Project, Type(name), random, depth + 1);
}

/// <summary>A native type with the arguments of one reference.</summary>
public sealed record BoundNative(INativeType Type, NativeArguments Args);

/// <summary>
/// What a generator writes for a native type. <see cref="Type"/>, <see cref="Read"/> and <see cref="Write"/> are
/// templates with placeholders in double braces:
/// <list type="bullet">
/// <item><c>{{reader}}</c>, <c>{{writer}}</c>: the reader or writer variable; <c>{{value}}</c>: the value to write.</item>
/// <item><c>{{pass-reader}}</c>, <c>{{pass-writer}}</c>: the variable as a method argument (<c>ref reader</c> for C# spans).</item>
/// <item><c>{{namespace}}</c>: the namespace (C#) or package (Java) of the <see cref="Files"/>; <c>{{runtime}}</c>: the runtime's.</item>
/// <item><c>{{parameter}}</c>: an argument as written; <c>{{parameter:pascal}}</c> in PascalCase (<c>java-network</c> → <c>JavaNetwork</c>).</item>
/// <item>For a type parameter <c>T</c>: <c>{{T:type}}</c>, the type of its values in the target language; <c>{{T:read}}</c>
/// and <c>{{T:write}}</c>, a function that reads a value from a reader, or writes one to a writer (see the generator's
/// documentation for their signatures). The functions can't use fields of the struct.</item>
/// </list>
/// </summary>
/// <remarks>
/// The same record describes the code of a definition that is bound to an existing type (<see cref="Codegen.TypeBinding"/>).
/// </remarks>
/// <param name="Type">The type of values in the target language.</param>
/// <param name="Read">An expression that reads a value; empty when there is only a <see cref="Codec"/>.</param>
/// <param name="Write">A statement that writes <c>{{value}}</c>, without the semicolon; empty when there is only a <see cref="Codec"/>.</param>
/// <param name="Files">Source files the generated code needs, written into the output (with the same placeholders).</param>
/// <param name="IsValueType">C#: the type is a struct (nullable as <c>T?</c> with <c>.Value</c>). A <see cref="Type"/> that is just <c>{{T:type}}</c> is a struct when <c>T</c>'s values are.</param>
public sealed record NativeCodegen(string Type, string Read, string Write, IReadOnlyList<NativeFile>? Files = null, bool IsValueType = false)
{
    /// <summary>
    /// An expression of a codec value that reads and writes values, for generators that compose codecs instead of calling
    /// read and write code (e.g. Minecraft's <c>StreamCodec</c>s); null when there is none.
    /// </summary>
    public string? Codec { get; init; }

    /// <summary>
    /// Bindings of enums: an expression of the wire value of <c>{{value}}</c>, so that expressions can compare and
    /// calculate with it (<c>{{value}}.ordinal()</c>); null when expressions can't use the values.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Whether there is code to read and write values (not only a <see cref="Codec"/>).</summary>
    public bool CanReadWrite => Read.Length > 0 && Write.Length > 0;
}

/// <param name="Name">The file name, e.g. <c>Color.cs</c>; it goes into the folder of <c>{{namespace}}</c>.</param>
public sealed record NativeFile(string Name, string Content);
