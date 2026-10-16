using System.Globalization;
using System.Text.RegularExpressions;
using FormatStudio.Model;
using FormatStudio.Natives;

namespace FormatStudio.Dsl;

/// <summary>
/// The short string form of type expressions: <c>u16be</c>, <c>Foo[]</c>, <c>Foo[4]</c>, <c>Foo[u8]</c>, <c>Foo[eof]</c>,
/// <c>Foo[count]</c>, <c>Foo?</c>, <c>string</c>, <c>bytes</c>, <c>nbt</c>, <c>nbt(java)</c>.
/// </summary>
public static partial class TypeExpressions
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)?$")]
    private static partial Regex TypeNameRegex();

    public static bool IsIdentifier(string text) => IdentifierRegex().IsMatch(text);

    /// <summary>A definition's name, or a library's definition as <c>Library.Name</c>.</summary>
    public static bool IsTypeName(string text) => TypeNameRegex().IsMatch(text);

    public static TypeRef Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new System.FormatException("Empty type expression");

        if (text.EndsWith('?')) return new OptionalRef(Parse(text[..^1]));

        if (text.EndsWith(']'))
        {
            var depth = 0;
            for (var i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == ']') depth++;
                else if (text[i] == '[' && --depth == 0)
                {
                    // bytes[n] is a byte string of length n; an array of byte strings needs {array: bytes}.
                    if (text[..i].Trim() == "bytes") return new BytesRef(ParseLength(text[(i + 1)..^1]));
                    var element = Parse(text[..i]);
                    return new ArrayRef(element, ParseLength(text[(i + 1)..^1]));
                }
            }

            throw new System.FormatException($"Unbalanced brackets in type '{text}'");
        }

        switch (text)
        {
            case "string": return new StringRef(null, LengthSpec.Default);
            case "bytes": return new BytesRef(LengthSpec.Default);
        }

        if (NativeTypes.IsBuiltIn(text)) return new NativeRef(text);
        if (text.EndsWith(')'))
        {
            var open = text.IndexOf('(');
            var name = open > 0 ? text[..open].Trim() : "";
            if (!IsTypeName(name)) throw new System.FormatException($"Invalid type expression '{text}'");
            var args = text[(open + 1)..^1];
            return new NativeRef(name, args.Trim().Length == 0 ? [] : SplitArguments(args).Select(a => a.Trim()).ToList());
        }

        if (Primitives.TryParse(text, out var primitive)) return primitive;
        if (!IsTypeName(text)) throw new System.FormatException($"Invalid type expression '{text}'");
        if ((text.EndsWith("le", StringComparison.Ordinal) || text.EndsWith("be", StringComparison.Ordinal))
            && Primitives.TryParse(text[..^2], out var single))
            throw new System.FormatException($"{single} has no byte order");
        return new NamedRef(text);
    }

    /// <summary>Parses a length: empty or <c>default</c>, a number, a primitive, <c>eof</c>, <c>zero</c> or an expression.</summary>
    public static LengthSpec ParseLength(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == "default") return LengthSpec.Default;
        if (text == "eof") return LengthSpec.Eof;
        if (text == "zero") return LengthSpec.Zero;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return new LengthSpec(LengthKind.Fixed, Fixed: count);
        if (Primitives.TryParse(text, out var prefix))
        {
            if (!Primitives.IsInteger(prefix.Kind))
                throw new System.FormatException($"Length prefix must be an integer type, not '{text}'");
            return new LengthSpec(LengthKind.Prefix, prefix);
        }

        return new LengthSpec(LengthKind.Expression, Expression: text);
    }

    public static string FormatLength(LengthSpec length) => length.Kind == LengthKind.Default ? "" : length.ToString();

    /// <summary>Returns the short string form, or null when the type needs the mapping form.</summary>
    public static string? TryFormat(TypeRef type)
    {
        switch (type)
        {
            case PrimitiveRef primitive:
                return primitive.ToString();
            case NamedRef { As: null, Tag: null } named:
                return named.Name;
            case StringRef { Encoding: null, Length.Kind: LengthKind.Default }:
                return "string";
            case BytesRef { Length.Kind: LengthKind.Default }:
                return "bytes";
            case BytesRef bytes when bytes.Length.Kind != LengthKind.Zero && !(bytes.Length.Expression?.Contains(']') ?? false):
                return $"bytes[{FormatLength(bytes.Length)}]";
            case ArrayRef { Element: BytesRef }:
                return null;
            case NativeRef { Args.Count: 0 } native when NativeTypes.IsBuiltIn(native.Name):
                return native.Name;
            case NativeRef native when native.Args.All(IsArgument):
                return native.ToString();
            case OptionalRef optional:
                return TryFormat(optional.Inner) is { } inner ? inner + "?" : null;
            case ArrayRef array when array.Length.Kind is not (LengthKind.Zero or LengthKind.Until):
                if (array.Length.Expression is { } expression && (expression.Contains('[') || expression.Contains(']')))
                    return null;
                return TryFormat(array.Element) is { } element ? $"{element}[{FormatLength(array.Length)}]" : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a native type argument can be written in the short form <c>name(a, b)</c>: no commas outside brackets
    /// and balanced brackets, so that type arguments such as <c>Chunk[u8]</c> or <c>zlib(Chunk)</c> fit.
    /// </summary>
    public static bool IsArgument(string text) =>
        text.Length > 0 && text.Trim() == text && text.IndexOfAny(['{', '}', '#', ':']) < 0 && SplitArguments(text) is [_] && Balanced(text);

    /// <summary>Splits arguments at the commas outside brackets.</summary>
    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    private static bool Balanced(string text)
    {
        var open = new Stack<char>();
        foreach (var c in text)
        {
            switch (c)
            {
                case '(' or '[':
                    open.Push(c);
                    break;
                case ')' or ']':
                    if (open.Count == 0 || open.Pop() != (c == ')' ? '(' : '[')) return false;
                    break;
            }
        }

        return open.Count == 0;
    }

    public static string EncodingName(StringEncoding encoding) => encoding switch
    {
        StringEncoding.Utf8 => "utf8",
        StringEncoding.Mutf8 => "mutf8",
        StringEncoding.Ascii => "ascii",
        StringEncoding.Latin1 => "latin1",
        StringEncoding.Utf16Le => "utf16le",
        _ => "utf16be",
    };

    public static StringEncoding ParseEncoding(string text) => text switch
    {
        "utf8" => StringEncoding.Utf8,
        "mutf8" => StringEncoding.Mutf8,
        "ascii" => StringEncoding.Ascii,
        "latin1" => StringEncoding.Latin1,
        "utf16le" => StringEncoding.Utf16Le,
        "utf16be" => StringEncoding.Utf16Be,
        _ => throw new System.FormatException($"Unknown string encoding '{text}' (utf8, mutf8, ascii, latin1, utf16le, utf16be)"),
    };
}
