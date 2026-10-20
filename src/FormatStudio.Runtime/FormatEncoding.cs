namespace FormatStudio.Runtime;

/// <summary>The text encodings generated code reads and writes strings in.</summary>
public enum FormatEncoding
{
    Utf8,
    /// <summary>7-bit ASCII: other bytes and characters are errors (or U+FFFD when reading leniently).</summary>
    Ascii,
    /// <summary>ISO 8859-1: every byte is a character; characters above U+00FF can't be written.</summary>
    Latin1,
}
