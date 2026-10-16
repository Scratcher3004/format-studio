using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Natives;

/// <summary>The native types a project can use, by name. Immutable; <see cref="With"/> adds one.</summary>
public sealed partial class NativeTypes
{
    private readonly Dictionary<string, INativeType> _types;
    private readonly Dictionary<(string Type, string Generator), NativeCodegenExtension> _extensions;

    private NativeTypes(Dictionary<string, INativeType> types, Dictionary<(string Type, string Generator), NativeCodegenExtension>? extensions = null)
    {
        _types = types;
        _extensions = extensions ?? [];
    }

    /// <summary>The types that come with Format Studio.</summary>
    public static NativeTypes BuiltIn { get; } = new(new Dictionary<string, INativeType>(StringComparer.Ordinal)
    {
        [NbtNative.Instance.Name] = NbtNative.Instance,
    });

    /// <summary>Whether a name is a built-in type, written without arguments as a keyword (<c>nbt</c>).</summary>
    public static bool IsBuiltIn(string name) => BuiltIn._types.ContainsKey(name);

    public IEnumerable<INativeType> All => _types.Values;

    public INativeType? Find(string name) => _types.GetValueOrDefault(name);

    /// <summary>The code for other generators that <c>native-codegen</c> files add to the types.</summary>
    public IEnumerable<NativeCodegenExtension> Extensions => _extensions.Values.Distinct();

    /// <summary>The code of a type for a generator: from a <c>native-codegen</c> file, else the type's own.</summary>
    public NativeCodegen? Codegen(INativeType type, string generator) =>
        _extensions.TryGetValue((type.Name, generator), out var extension) ? extension.Codegen[generator] : type.Codegen(generator);

    /// <summary>A copy with <paramref name="type"/> added, or replacing the one with its name.</summary>
    public NativeTypes With(INativeType type) =>
        new(new Dictionary<string, INativeType>(_types, StringComparer.Ordinal) { [type.Name] = type }, _extensions);

    /// <summary>
    /// A copy with the code of <paramref name="extensions"/> added. An extension is left out, and reported to
    /// <paramref name="error"/>, when its type is unknown, has code of its own for one of its generators or another
    /// extension has code for the type and generator; adding the same extension again changes nothing.
    /// </summary>
    public NativeTypes WithCodegen(IEnumerable<NativeCodegenExtension> extensions, Action<NativeCodegenExtension, string> error)
    {
        var result = new Dictionary<(string Type, string Generator), NativeCodegenExtension>(_extensions);
        foreach (var extension in extensions)
        {
            if (result.ContainsValue(extension)) continue;
            if (Find(extension.Type) is not { } type)
            {
                error(extension, $"native-codegen for unknown native type '{extension.Type}'");
                continue;
            }

            var problems = new List<string>();
            foreach (var generator in extension.Codegen.Keys)
            {
                if (result.TryGetValue((type.Name, generator), out var other))
                    problems.Add($"{type.Name} has {generator} code from both {other.Source.File} and {extension.Source.File}");
                else if (OwnCodegen(type, generator))
                    problems.Add($"{type.Name} has {generator} code of its own; native-codegen only adds generators a type has no code for");
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems) error(extension, problem);
                continue;
            }

            foreach (var generator in extension.Codegen.Keys) result[(type.Name, generator)] = extension;
        }

        return new NativeTypes(_types, result);
    }

    private static bool OwnCodegen(INativeType type, string generator)
    {
        try
        {
            return type.Codegen(generator) != null;
        }
        catch (FormatDefinitionException)
        {
            return false;
        }
    }

    /// <summary>
    /// A copy with <paramref name="types"/> added. A type named like another one (except the same type again) is left
    /// out and reported to <paramref name="conflict"/>.
    /// </summary>
    public NativeTypes With(IEnumerable<INativeType> types, Action<INativeType, INativeType> conflict)
    {
        var result = new Dictionary<string, INativeType>(_types, StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (result.TryGetValue(type.Name, out var existing))
            {
                if (existing != type) conflict(existing, type);
                continue;
            }

            result[type.Name] = type;
        }

        return new NativeTypes(result, _extensions);
    }

    /// <summary>
    /// The type of a reference with its arguments completed: an argument left out (or written as <c>default</c>) comes
    /// from the project's defaults for the type, then from the parameter's default. Throws when a required argument is
    /// missing or an argument doesn't fit its parameter.
    /// </summary>
    public BoundNative Bind(NativeRef native, FormatDefaults defaults)
    {
        var type = Find(native.Name) ?? throw new FormatDefinitionException($"Unknown native type '{native.Name}'");
        var parameters = type.Parameters;
        if (native.Args.Count > parameters.Count)
            throw new FormatDefinitionException($"{native.Name} takes {Count(parameters.Count)}, not {native.Args.Count}");
        var defaultArgs = defaults.Natives.GetValueOrDefault(native.Name) ?? [];
        if (defaultArgs.Count > parameters.Count)
            throw new FormatDefinitionException($"defaults.{native.Name}: {native.Name} takes {Count(parameters.Count)}, not {defaultArgs.Count}");
        var values = new string[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var value = Given(native.Args, i) ?? Given(defaultArgs, i) ?? parameter.Default
                ?? throw new FormatDefinitionException(parameter.Missing
                    ?? $"{native.Name} needs a {parameter.Name}: write {native.Name}(<{parameter.Name}>) or set defaults.{native.Name} in the project");
            Check(native.Name, parameter, value);
            values[i] = value;
        }

        return new BoundNative(type, new NativeArguments(parameters, values));
    }

    private static string? Given(IReadOnlyList<string> args, int index) =>
        index < args.Count && args[index] is var arg && arg.Trim() is not ("" or "default") ? arg.Trim() : null;

    private static string Count(int count) => count == 1 ? "1 argument" : $"{count} arguments";

    private static void Check(string name, NativeParameter parameter, string value)
    {
        switch (parameter.Kind)
        {
            case NativeParameterKind.Choice when parameter.Choices != null && !parameter.Choices.Contains(value):
                throw new FormatDefinitionException($"Unknown {parameter.Name} '{value}' for {name} ({string.Join(", ", parameter.Choices)})");
            case NativeParameterKind.Integer when !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _):
                throw new FormatDefinitionException($"The {parameter.Name} of {name} must be an integer, not '{value}'");
        }
    }

    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_-]*)(:pascal)?\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Fills the placeholders of a <see cref="NativeCodegen"/> template: <paramref name="values"/> by name, then the
    /// arguments. Unknown placeholders stay as they are.
    /// </summary>
    public static string Fill(string template, NativeArguments? args, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            string? value = values.GetValueOrDefault(name);
            if (value == null && args != null)
            {
                for (var i = 0; i < args.Parameters.Count; i++)
                {
                    if (args.Parameters[i].Name == name) value = args.Values[i];
                }
            }

            if (value == null) return match.Value;
            return match.Groups[2].Success ? Pascal(value) : value;
        });

    /// <summary><c>java-network</c> → <c>JavaNetwork</c>.</summary>
    public static string Pascal(string text)
    {
        var builder = new StringBuilder(text.Length);
        var upper = true;
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }
}
