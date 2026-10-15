using System.Text;

namespace FormatStudio.Codegen;

/// <summary>Builds indented source text for languages with braces.</summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0) _builder.Append(' ', _indent * 4).Append(text);
        _builder.Append('\n');
        return this;
    }

    /// <summary>A line followed by <c>{</c> on its own line (C# style).</summary>
    public CodeWriter Open(string text)
    {
        Line(text);
        Line("{");
        _indent++;
        return this;
    }

    /// <summary>A line ending with <c> {</c> (Java style).</summary>
    public CodeWriter OpenInline(string text)
    {
        Line(text + " {");
        _indent++;
        return this;
    }

    public CodeWriter Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
        return this;
    }

    public void Indent() => _indent++;
    public void Outdent() => _indent--;

    public override string ToString() => _builder.ToString();
}
