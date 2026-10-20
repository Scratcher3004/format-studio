using System.Text;

namespace FormatStudio.Runtime;

/// <summary>Text decoding and encoding, and the padding and fill checks shared by the readers and writers.</summary>
internal static class TextCodec
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictAscii = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private static readonly Encoding LenientAscii = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("\uFFFD"));
    private static readonly Encoding StrictLatin1 = Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static Encoding Of(FormatEncoding encoding, bool strict) => encoding switch
    {
        FormatEncoding.Utf8 => strict ? StrictUtf8 : Encoding.UTF8,
        FormatEncoding.Ascii => strict ? StrictAscii : LenientAscii,
        _ => StrictLatin1,
    };

    private static string Name(FormatEncoding encoding) => encoding switch
    {
        FormatEncoding.Utf8 => "UTF-8",
        FormatEncoding.Ascii => "ASCII",
        _ => "Latin-1",
    };

    /// <summary>Decodes text; <paramref name="strict"/> rejects invalid bytes instead of replacing them with U+FFFD.</summary>
    /// <param name="offset">Where the bytes start, for the message.</param>
    public static string Decode(ReadOnlySpan<byte> bytes, FormatEncoding encoding, bool strict, long offset)
    {
        try
        {
            return Of(encoding, strict).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatDataException($"Invalid {Name(encoding)} in the string at offset {offset}");
        }
    }

    /// <summary>The number of bytes <see cref="Encode"/> gives.</summary>
    public static int ByteCount(string value, FormatEncoding encoding) =>
        encoding == FormatEncoding.Utf8 ? Encoding.UTF8.GetByteCount(value) : value.Length;

    /// <summary>Encodes text; characters the encoding doesn't have (above U+007F for ASCII, U+00FF for Latin-1) are errors.</summary>
    public static byte[] Encode(string value, FormatEncoding encoding)
    {
        try
        {
            return Of(encoding, true).GetBytes(value);
        }
        catch (EncoderFallbackException e) when (encoding != FormatEncoding.Utf8)
        {
            throw new FormatDataException($"'{e.CharUnknown}' can't be written as {Name(encoding)}");
        }
        catch (EncoderFallbackException)
        {
            return Encoding.UTF8.GetBytes(value);
        }
    }

    /// <summary>
    /// The string in a field that ends at the first zero byte, or with the field; <paramref name="checkPadding"/> rejects
    /// non-zero bytes after that zero.
    /// </summary>
    public static string Terminated(ReadOnlySpan<byte> field, FormatEncoding encoding, bool strict, bool checkPadding, long offset)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0) return Decode(field, encoding, strict, offset);
        var bad = checkPadding ? field[end..].IndexOfAnyExcept((byte)0) : -1;
        if (bad >= 0) throw new FormatDataException($"Padding byte 0x{field[end + bad]:x2} at offset {offset + end + bad} after the string terminator");
        return Decode(field[..end], encoding, strict, offset);
    }

    /// <summary>With <paramref name="check"/>, fails when <paramref name="fill"/> isn't <paramref name="pattern"/> repeated.</summary>
    public static void CheckFill(ReadOnlySpan<byte> fill, ReadOnlySpan<byte> pattern, bool check, long offset)
    {
        if (!check) return;
        for (var i = 0; i < fill.Length; i++)
        {
            if (fill[i] != pattern[i % pattern.Length])
                throw new FormatDataException($"Byte 0x{fill[i]:x2} at offset {offset + i} of the region is not its fill");
        }
    }
}
