using System.Text.Json.Nodes;
using FormatStudio.Model;

namespace FormatStudio.Codegen;

public enum CodegenOptionKind
{
    Text,
    Bool,
    /// <summary>A list of names.</summary>
    List,
    /// <summary>One of <see cref="CodegenOption.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// An option of a generator (a key of the target's <c>options</c>). The UI builds its form from these, so every option a
/// generator reads should be described here.
/// </summary>
/// <param name="Default">The value used when the option is missing (text or bool; lists default to empty).</param>
/// <param name="Advanced">Rarely changed, e.g. the names of a custom runtime.</param>
/// <param name="Choices">The allowed values of a <see cref="CodegenOptionKind.Choice"/>.</param>
public sealed record CodegenOption(string Key, string Label, CodegenOptionKind Kind, string Description, JsonNode? Default = null, bool Advanced = false,
    IReadOnlyList<string>? Choices = null)
{
    public static CodegenOption Text(string key, string label, string defaultValue, string description, bool advanced = false) =>
        new(key, label, CodegenOptionKind.Text, description, JsonValue.Create(defaultValue), advanced);

    public static CodegenOption Bool(string key, string label, bool defaultValue, string description, bool advanced = false) =>
        new(key, label, CodegenOptionKind.Bool, description, JsonValue.Create(defaultValue), advanced);

    public static CodegenOption List(string key, string label, string description, bool advanced = false) =>
        new(key, label, CodegenOptionKind.List, description, null, advanced);

    /// <param name="choices">The allowed values; the first is the default.</param>
    public static CodegenOption Choice(string key, string label, IReadOnlyList<string> choices, string description, bool advanced = false) =>
        new(key, label, CodegenOptionKind.Choice, description, JsonValue.Create(choices[0]), advanced, choices);

    /// <summary>Whether a value is the same as leaving the option out (so a saved target doesn't need it).</summary>
    public bool IsDefault(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue text when text.TryGetValue<string>(out var s) && s.Length == 0 => Kind is CodegenOptionKind.Text or CodegenOptionKind.Choice,
        _ => JsonNode.DeepEquals(value, Default),
    };
}

/// <summary>Reads typed values from a target's options, with the defaults of the option descriptions.</summary>
public sealed class OptionReader(JsonObject options, IReadOnlyList<CodegenOption> descriptions)
{
    private CodegenOption Describe(string key) =>
        descriptions.FirstOrDefault(o => o.Key == key) ?? throw new InvalidOperationException($"Option '{key}' is not described");

    public string Text(string key)
    {
        var value = options[key];
        if (value is JsonValue json && json.TryGetValue<string>(out var text) && text.Length > 0) return text;
        if (value is JsonValue other && other.GetValueKind() is System.Text.Json.JsonValueKind.Number) return other.ToJsonString();
        if (value != null && value is not JsonValue) throw new FormatDefinitionException($"Option '{key}' must be text");
        return Describe(key).Default?.GetValue<string>() ?? "";
    }

    public string Choice(string key)
    {
        var text = Text(key);
        var choices = Describe(key).Choices ?? [];
        return choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatDefinitionException($"Option '{key}' must be one of {string.Join(", ", choices)}, not '{text}'");
    }

    public bool Bool(string key)
    {
        return options[key] switch
        {
            null => Describe(key).Default?.GetValue<bool>() ?? false,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) => parsed,
            _ => throw new FormatDefinitionException($"Option '{key}' must be true or false"),
        };
    }

    public HashSet<string> List(string key)
    {
        Describe(key);
        return options[key] switch
        {
            null => new HashSet<string>(StringComparer.Ordinal),
            JsonArray array => array.Where(e => e != null).Select(e => e!.ToString()).ToHashSet(StringComparer.Ordinal),
            JsonValue value when value.TryGetValue<string>(out var text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal),
            _ => throw new FormatDefinitionException($"Option '{key}' must be a list of names"),
        };
    }
}
