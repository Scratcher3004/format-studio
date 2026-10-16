using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

/// <summary>
/// What the decoder offers to code that reads a value the DSL can't describe (NBT, and the types of native libraries):
/// the bytes, the value's span in the structure view, nested DSL types and diagnostics. One instance covers one value.
/// </summary>
public interface INativeReader
{
    FormatProject Project { get; }

    /// <summary>The bytes, positioned at the value. The value's span ends where reading stops.</summary>
    WireReader Wire { get; }

    /// <summary>The value's path, e.g. <c>items[2].tag</c>.</summary>
    string Path { get; }

    /// <summary>Sets the short text shown for the value in the structure view.</summary>
    void Display(string text);

    /// <summary>Reads a value of a DSL type at the current position, as a child span called <paramref name="name"/>.</summary>
    JsonNode? Read(TypeRef type, string name);

    /// <summary>
    /// Reads a value of a DSL type from other bytes than the input, e.g. after decompressing them. All bytes must be
    /// read; errors are reported at the current position.
    /// </summary>
    JsonNode? Decode(TypeRef type, byte[] data, string name);

    void Warning(string message);

    /// <summary>An error at the current position, for the caller to throw.</summary>
    DecodeException Error(string message);
}

/// <summary>What the encoder offers to code that writes a value the DSL can't describe; see <see cref="INativeReader"/>.</summary>
public interface INativeWriter
{
    FormatProject Project { get; }

    /// <summary>The bytes written so far; the value is appended.</summary>
    WireWriter Wire { get; }

    /// <summary>The value's path, e.g. <c>items[2].tag</c>.</summary>
    string Path { get; }

    /// <summary>Writes a value of a DSL type; <paramref name="name"/> extends the path in error messages.</summary>
    void Write(TypeRef type, JsonNode? value, string name);

    /// <summary>Writes a value of a DSL type to new bytes instead of the output, e.g. to compress them.</summary>
    byte[] Encode(TypeRef type, JsonNode? value, string name);

    void Warning(string message);

    /// <summary>An error for the value, for the caller to throw.</summary>
    EncodeException Error(string message);
}
