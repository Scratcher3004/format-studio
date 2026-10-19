using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FormatStudio.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FormatStudio.Dsl;

/// <summary>Conversion between YamlDotNet nodes and JSON nodes, and plain scalar interpretation.</summary>
public static partial class YamlValues
{
    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex("^[-+]?0x[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[-+]?0b[01]+$")]
    private static partial Regex BinaryRegex();

    [GeneratedRegex(@"^[-+]?([0-9]*\.[0-9]+|[0-9]+\.[0-9]*|[0-9]+)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    /// <summary>Interprets a plain (unquoted) scalar: null, bool, integer, float or string.</summary>
    public static JsonNode? InterpretPlain(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
            case ".inf" or "+.inf": return JsonValue.Create(double.PositiveInfinity);
            case "-.inf": return JsonValue.Create(double.NegativeInfinity);
            case ".nan": return JsonValue.Create(double.NaN);
        }

        if (IntegerRegex().IsMatch(text))
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
            if (ulong.TryParse(text.TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var ul))
                return JsonValue.Create(ul);
            return JsonValue.Create(decimal.Parse(text, CultureInfo.InvariantCulture));
        }

        if (HexRegex().IsMatch(text) || BinaryRegex().IsMatch(text))
        {
            if (TryParseInteger(text, out var integer, out _)) return JsonValue.Create(integer);
            if (text[0] != '-' && ParseMagnitude(text.TrimStart('+')[2..], text.Contains('x') ? 16 : 2) is { } large)
                return JsonValue.Create(large);
        }

        if (FloatRegex().IsMatch(text))
            return JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture));

        return JsonValue.Create(text);
    }

    /// <summary>Reads a decimal, hex (<c>0x1F</c>) or binary (<c>0b101</c>) integer, optionally signed, and the base it was written in.</summary>
    public static bool TryParseInteger(string? text, out long value, out NumberBase numberBase)
    {
        value = 0;
        numberBase = NumberBase.Decimal;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (IntegerRegex().IsMatch(text))
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        var hex = HexRegex().IsMatch(text);
        if (!hex && !BinaryRegex().IsMatch(text)) return false;
        var negative = text[0] == '-';
        var digits = text.TrimStart('-', '+')[2..];
        if (ParseMagnitude(digits, hex ? 16 : 2) is not { } magnitude) return false;
        if (negative ? magnitude > (ulong)long.MaxValue + 1 : magnitude > long.MaxValue) return false;
        value = unchecked(negative ? (long)(0 - magnitude) : (long)magnitude);
        numberBase = hex ? NumberBase.Hex : NumberBase.Binary;
        return true;
    }

    private static ulong? ParseMagnitude(string digits, int radix) =>
        ulong.TryParse(digits, radix == 16 ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowBinarySpecifier,
            CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>Writes an integer in the base, e.g. <c>0x1F</c>, <c>-0x10</c> or <c>0b101</c>. <see cref="TryParseInteger"/> reads it back.</summary>
    public static string FormatInteger(long value, NumberBase numberBase)
    {
        if (numberBase == NumberBase.Decimal) return value.ToString(CultureInfo.InvariantCulture);
        var magnitude = unchecked(value < 0 ? 0 - (ulong)value : (ulong)value);
        var digits = numberBase == NumberBase.Hex
            ? "0x" + magnitude.ToString("X2", CultureInfo.InvariantCulture)
            : "0b" + Convert.ToString(unchecked((long)magnitude), 2);
        return value < 0 ? "-" + digits : digits;
    }

    /// <summary>True when the text, written as a plain scalar, would not read back as this string.</summary>
    public static bool NeedsQuotes(string text) => InterpretPlain(text) is not JsonValue value
        || value.GetValueKind() != JsonValueKind.String;

    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Style is ScalarStyle.Plain
            ? InterpretPlain(scalar.Value ?? "")
            : JsonValue.Create(scalar.Value ?? ""),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlMappingNode mapping => ToObject(mapping),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode mapping)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in mapping.Children)
            obj[((YamlScalarNode)key).Value ?? ""] = ToJson(value);
        return obj;
    }

    public static JsonNode? ParseYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : ToJson(stream.Documents[0].RootNode);
    }
}

/// <summary>An integer that <see cref="YamlEmitter"/> writes in the given base.</summary>
public sealed record YamlInteger(long Value, NumberBase Base);

/// <summary>An ordered YAML mapping for <see cref="YamlEmitter"/>.</summary>
public sealed class YMap : List<KeyValuePair<string, object?>>
{
    /// <summary>Write as <c>{a: b, c: d}</c> when every value is a scalar.</summary>
    public bool Flow { get; init; }

    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));

    /// <summary>Adds the entry unless the value is null, false or an empty list.</summary>
    public void AddIf(string key, object? value)
    {
        if (value is null or false or System.Collections.ICollection { Count: 0 }) return;
        Add(key, value);
    }
}

/// <summary>
/// A small YAML writer with predictable output: block mappings and sequences, flow mappings for small type
/// expressions, literal blocks for multi-line text. Values are strings, numbers, bools, null, <see cref="YMap"/>,
/// lists and <see cref="JsonNode"/>s.
/// </summary>
public static class YamlEmitter
{
    private static readonly JsonSerializerOptions JsonStringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(object? root)
    {
        var builder = new StringBuilder();
        WriteBlock(builder, root, 0);
        return builder.ToString();
    }

    /// <summary>The value on one line in flow style, e.g. <c>{x: 1, tags: [a, b]}</c>.</summary>
    public static string WriteFlow(object? value) => Normalize(value) switch
    {
        YMap map => "{" + string.Join(", ", map.Select(pair => $"{Key(pair.Key)}: {WriteFlow(pair.Value)}")) + "}",
        List<object?> list => "[" + string.Join(", ", list.Select(WriteFlow)) + "]",
        var scalar => Scalar(scalar, true),
    };

    private static void WriteBlock(StringBuilder builder, object? value, int indent)
    {
        switch (Normalize(value))
        {
            case YMap map:
                foreach (var (key, item) in map)
                {
                    builder.Append(' ', indent).Append(Key(key)).Append(':');
                    WriteAfterKey(builder, item, indent);
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    builder.Append(' ', indent).Append('-');
                    var normalized = Normalize(item);
                    if (normalized is YMap { Flow: false, Count: > 0 } map)
                    {
                        // "- first: x" then the remaining keys aligned with "first".
                        var builderItem = new StringBuilder();
                        WriteBlock(builderItem, map, indent + 2);
                        builder.Append(' ').Append(builderItem.ToString(indent + 2, builderItem.Length - indent - 2));
                    }
                    else
                    {
                        WriteAfterKey(builder, normalized, indent);
                    }
                }

                break;
            default:
                builder.Append(' ', indent).Append(Scalar(Normalize(value), false)).Append('\n');
                break;
        }
    }

    private static void WriteAfterKey(StringBuilder builder, object? value, int indent)
    {
        value = Normalize(value);
        switch (value)
        {
            case YMap { Count: 0 }:
                builder.Append(" {}\n");
                break;
            case List<object?> { Count: 0 }:
                builder.Append(" []\n");
                break;
            case YMap { Flow: true } map when map.All(pair => IsScalar(Normalize(pair.Value))):
                builder.Append(' ').Append(FlowMap(map)).Append('\n');
                break;
            case YMap or List<object?>:
                builder.Append('\n');
                WriteBlock(builder, value, indent + 2);
                break;
            case string text when text.Contains('\n') && CanUseLiteral(text):
                builder.Append(" |").Append(text.EndsWith('\n') ? "" : "-").Append('\n');
                foreach (var line in text.TrimEnd('\n').Split('\n'))
                {
                    if (line.Length > 0) builder.Append(' ', indent + 2).Append(line);
                    builder.Append('\n');
                }

                break;
            default:
                builder.Append(' ').Append(Scalar(value, false)).Append('\n');
                break;
        }
    }

    private static string FlowMap(YMap map) =>
        "{" + string.Join(", ", map.Select(pair => $"{Key(pair.Key)}: {Scalar(Normalize(pair.Value), true)}")) + "}";

    private static bool IsScalar(object? value) => value is not (YMap or List<object?>);

    private static bool CanUseLiteral(string text)
    {
        if (text.StartsWith(' ') || text.StartsWith('\n')) return false;
        foreach (var line in text.TrimEnd('\n').Split('\n'))
        {
            if (line.EndsWith(' ') || line.EndsWith('\t')) return false;
            if (line.Any(c => char.IsControl(c) && c != '\t')) return false;
        }

        return !text.EndsWith("\n\n");
    }

    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case JsonObject obj:
                var map = new YMap();
                foreach (var (key, item) in obj) map.Add(key, item);
                return map;
            case JsonArray array:
                return array.Select(item => (object?)item).ToList();
            case JsonValue json:
                return json.GetValueKind() switch
                {
                    JsonValueKind.String => json.GetValue<string>(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => new RawNumber(json.ToJsonString()),
                    _ => null,
                };
            case System.Collections.IList list and not YMap:
                return list.Cast<object?>().ToList();
            default:
                return value;
        }
    }

    private sealed record RawNumber(string Text);

    private static string Key(string key) => NeedsQuoting(key, true) ? Quote(key) : key;

    private static string Scalar(object? value, bool flow) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        RawNumber number => number.Text,
        YamlInteger integer => YamlValues.FormatInteger(integer.Value, integer.Base),
        string s => NeedsQuoting(s, flow) ? Quote(s) : s,
        double d => double.IsPositiveInfinity(d) ? ".inf" : double.IsNegativeInfinity(d) ? "-.inf" : double.IsNaN(d) ? ".nan"
            : d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? ""),
    };

    private static string Quote(string text) => JsonSerializer.Serialize(text, JsonStringOptions);

    private static bool NeedsQuoting(string text, bool flow)
    {
        if (text.Length == 0 || YamlValues.NeedsQuotes(text)) return true;
        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) return true;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(text[0])) return true;
        if (text.Contains(": ") || text.Contains(" #") || text.EndsWith(':')) return true;
        if (flow && text.IndexOfAny([',', '[', ']', '{', '}']) >= 0) return true;
        return text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF');
    }
}
