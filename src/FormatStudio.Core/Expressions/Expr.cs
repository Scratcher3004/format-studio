using System.Globalization;
using System.Text;

namespace FormatStudio.Expressions;

/// <summary>
/// The expression language of conditions, lengths, computed fields and external variant tags. C-like operators,
/// <c>a.b</c> member access, <c>a[i]</c> indexing, <c>Enum::Member</c>, <c>_parent</c>/<c>_root</c> and the functions
/// <c>len</c>, <c>bytelen</c> and <c>tagof</c>.
/// </summary>
public abstract record Expr
{
    public static Expr Parse(string text) => new ExprParser(text).ParseAll();

    public static bool TryParse(string text, out Expr? expression, out string? error)
    {
        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (ExprException e)
        {
            expression = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>All plain names this expression reads (not members, functions or enum constants).</summary>
    public IEnumerable<string> Names()
    {
        switch (this)
        {
            case NameExpr name:
                yield return name.Name;
                break;
            case MemberExpr member:
                foreach (var n in member.Target.Names()) yield return n;
                break;
            case IndexExpr index:
                foreach (var n in index.Target.Names().Concat(index.Index.Names())) yield return n;
                break;
            case UnaryExpr unary:
                foreach (var n in unary.Operand.Names()) yield return n;
                break;
            case BinaryExpr binary:
                foreach (var n in binary.Left.Names().Concat(binary.Right.Names())) yield return n;
                break;
            case ConditionalExpr conditional:
                foreach (var n in conditional.Condition.Names().Concat(conditional.Then.Names()).Concat(conditional.Else.Names()))
                    yield return n;
                break;
            case CallExpr call:
                foreach (var n in call.Arguments.SelectMany(a => a.Names())) yield return n;
                break;
        }
    }
}

public sealed record LiteralExpr(object? Value) : Expr
{
    public override string ToString() => Value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Value.ToString()!,
    };
}

public sealed record NameExpr(string Name) : Expr
{
    public override string ToString() => Name;
}

public sealed record EnumConstExpr(string Enum, string Member) : Expr
{
    public override string ToString() => $"{Enum}::{Member}";
}

public sealed record MemberExpr(Expr Target, string Name) : Expr
{
    public override string ToString() => $"{Target}.{Name}";
}

public sealed record IndexExpr(Expr Target, Expr Index) : Expr
{
    public override string ToString() => $"{Target}[{Index}]";
}

public sealed record UnaryExpr(string Operator, Expr Operand) : Expr
{
    public override string ToString() => $"{Operator}({Operand})";
}

public sealed record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr
{
    public override string ToString() => $"({Left} {Operator} {Right})";
}

public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else) : Expr
{
    public override string ToString() => $"({Condition} ? {Then} : {Else})";
}

public sealed record CallExpr(string Function, IReadOnlyList<Expr> Arguments) : Expr
{
    public override string ToString() => $"{Function}({string.Join(", ", Arguments)})";
}

public sealed class ExprException(string message) : Exception(message);

internal sealed class ExprParser(string text)
{
    private int _position;

    public Expr ParseAll()
    {
        var expression = ParseConditional();
        SkipSpace();
        if (_position < text.Length) throw Error($"Unexpected '{text[_position]}'");
        return expression;
    }

    private ExprException Error(string message) => new($"{message} at position {_position + 1} in '{text}'");

    private void SkipSpace()
    {
        while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
    }

    private bool Peek(string token)
    {
        SkipSpace();
        return string.CompareOrdinal(text, _position, token, 0, token.Length) == 0;
    }

    private bool Accept(string token, params string[] notFollowedBy)
    {
        if (!Peek(token)) return false;
        foreach (var longer in notFollowedBy)
            if (string.CompareOrdinal(text, _position, longer, 0, longer.Length) == 0) return false;
        _position += token.Length;
        return true;
    }

    private void Expect(string token)
    {
        if (!Accept(token)) throw Error($"Expected '{token}'");
    }

    private Expr ParseConditional()
    {
        var condition = ParseBinary(0);
        if (!Accept("?")) return condition;
        var then = ParseConditional();
        Expect(":");
        var otherwise = ParseConditional();
        return new ConditionalExpr(condition, then, otherwise);
    }

    // Operator levels, lowest precedence first. Each entry: operator and the longer operators it must not prefix.
    private static readonly (string Op, string[] Not)[][] Levels =
    [
        [("||", [])],
        [("&&", [])],
        [("|", ["||"])],
        [("^", [])],
        [("&", ["&&"])],
        [("==", []), ("!=", [])],
        [("<=", []), (">=", []), ("<", ["<<", "<="]), (">", [">>", ">="])],
        [("<<", []), (">>", [])],
        [("+", []), ("-", [])],
        [("*", []), ("/", []), ("%", [])],
    ];

    private Expr ParseBinary(int level)
    {
        if (level == Levels.Length) return ParseUnary();
        var left = ParseBinary(level + 1);
        while (true)
        {
            var matched = Levels[level].FirstOrDefault(op => Accept(op.Op, op.Not));
            if (matched.Op == null) return left;
            left = new BinaryExpr(matched.Op, left, ParseBinary(level + 1));
        }
    }

    private Expr ParseUnary()
    {
        if (Accept("!", "!=")) return new UnaryExpr("!", ParseUnary());
        if (Accept("-")) return new UnaryExpr("-", ParseUnary());
        if (Accept("~")) return new UnaryExpr("~", ParseUnary());
        return ParsePostfix(ParsePrimary());
    }

    private Expr ParsePostfix(Expr expression)
    {
        while (true)
        {
            if (Accept("."))
            {
                expression = new MemberExpr(expression, ParseIdentifier());
            }
            else if (Accept("["))
            {
                var index = ParseConditional();
                Expect("]");
                expression = new IndexExpr(expression, index);
            }
            else
            {
                return expression;
            }
        }
    }

    private string ParseIdentifier()
    {
        SkipSpace();
        var start = _position;
        if (_position < text.Length && (char.IsLetter(text[_position]) || text[_position] == '_'))
        {
            _position++;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
        }

        if (start == _position) throw Error("Expected a name");
        return text[start.._position];
    }

    private Expr ParsePrimary()
    {
        SkipSpace();
        if (_position >= text.Length) throw Error("Unexpected end of expression");
        var c = text[_position];

        if (Accept("("))
        {
            var inner = ParseConditional();
            Expect(")");
            return inner;
        }

        if (char.IsDigit(c)) return ParseNumber();
        if (c is '"' or '\'') return ParseString(c);

        var name = ParseIdentifier();
        switch (name)
        {
            case "true": return new LiteralExpr(true);
            case "false": return new LiteralExpr(false);
            case "null": return new LiteralExpr(null);
        }

        if (Accept("::")) return new EnumConstExpr(name, ParseIdentifier());
        if (!Accept("(")) return new NameExpr(name);

        var arguments = new List<Expr>();
        if (!Accept(")"))
        {
            do arguments.Add(ParseConditional());
            while (Accept(","));
            Expect(")");
        }

        return new CallExpr(name, arguments);
    }

    private Expr ParseNumber()
    {
        var start = _position;
        if (text[_position] == '0' && _position + 1 < text.Length && text[_position + 1] is 'x' or 'X')
        {
            _position += 2;
            while (_position < text.Length && Uri.IsHexDigit(text[_position])) _position++;
            return new LiteralExpr((decimal)ulong.Parse(text.AsSpan(start + 2, _position - start - 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture));
        }

        while (_position < text.Length && char.IsDigit(text[_position])) _position++;
        var isFloat = false;
        if (_position + 1 < text.Length && text[_position] == '.' && char.IsDigit(text[_position + 1]))
        {
            isFloat = true;
            _position++;
            while (_position < text.Length && char.IsDigit(text[_position])) _position++;
        }

        var number = text[start.._position];
        return isFloat
            ? new LiteralExpr(double.Parse(number, CultureInfo.InvariantCulture))
            : new LiteralExpr(decimal.Parse(number, CultureInfo.InvariantCulture));
    }

    private Expr ParseString(char quote)
    {
        _position++;
        var builder = new StringBuilder();
        while (_position < text.Length && text[_position] != quote)
        {
            if (text[_position] == '\\' && _position + 1 < text.Length) _position++;
            builder.Append(text[_position++]);
        }

        if (_position >= text.Length) throw Error("Unterminated string");
        _position++;
        return new LiteralExpr(builder.ToString());
    }
}
