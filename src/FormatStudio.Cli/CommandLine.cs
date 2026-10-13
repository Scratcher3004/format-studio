namespace FormatStudio.Cli;

/// <summary>
/// Minimal command-line parser: positional arguments, <c>--flag</c> switches and <c>--option value</c> pairs.
/// Options may be repeated.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public static CommandLine Parse(IEnumerable<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var result = new CommandLine();
        using var enumerator = args.GetEnumerator();

        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (flagOptions.Contains(name))
            {
                result._flags.Add(name);
            }
            else if (valueOptions.Contains(name))
            {
                if (!enumerator.MoveNext()) throw new UsageException($"Option --{name} requires a value.");
                if (!result._options.TryGetValue(name, out var values)) result._options[name] = values = [];
                values.Add(enumerator.Current);
            }
            else
            {
                throw new UsageException($"Unknown option --{name}.");
            }
        }

        return result;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public IReadOnlyList<string> All(string option) => _options.TryGetValue(option, out var values) ? values : [];

    public string? Single(string option)
    {
        var values = All(option);
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw new UsageException($"Option --{option} may only be given once."),
        };
    }

    public int Int(string option, int fallback)
    {
        var text = Single(option);
        if (text == null) return fallback;
        return int.TryParse(text, out var value) ? value : throw new UsageException($"--{option} expects a number (got '{text}').");
    }

    public void MaxPositional(int count, string usage)
    {
        if (Positional.Count > count) throw new UsageException($"Too many arguments. Usage: {usage}");
    }
}

internal sealed class UsageException(string message) : Exception(message);
