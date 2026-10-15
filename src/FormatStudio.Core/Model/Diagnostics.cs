namespace FormatStudio.Model;

public enum Severity
{
    Info,
    Warning,
    Error,
}

public sealed record Diagnostic(Severity Severity, string Message, SourceLocation? Source = null, string? Path = null)
{
    /// <summary>The <see cref="Area"/> of findings about the project's protocols.</summary>
    public const string ProtocolsArea = "protocols";

    /// <summary>The part of the project to fix the finding in, when it isn't the file of <see cref="Source"/>.</summary>
    public string? Area { get; init; }

    public override string ToString()
    {
        var where = Source != null ? $"{Source}: " : "";
        var path = Path != null ? $" [{Path}]" : "";
        return $"{where}{Severity.ToString().ToLowerInvariant()}: {Message}{path}";
    }
}

public sealed class DiagnosticBag : List<Diagnostic>
{
    public bool HasErrors => this.Any(d => d.Severity == Severity.Error);

    public void Error(string message, SourceLocation? source = null, string? path = null) =>
        Add(new Diagnostic(Severity.Error, message, source, path));

    public void Warning(string message, SourceLocation? source = null, string? path = null) =>
        Add(new Diagnostic(Severity.Warning, message, source, path));

    public void Info(string message, SourceLocation? source = null, string? path = null) =>
        Add(new Diagnostic(Severity.Info, message, source, path));
}

/// <summary>An error in a format definition or project file.</summary>
public sealed class FormatDefinitionException(string message, SourceLocation? source = null) : Exception(message)
{
    public SourceLocation? Location { get; } = source;
}
