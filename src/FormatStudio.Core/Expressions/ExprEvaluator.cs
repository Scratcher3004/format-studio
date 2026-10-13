using System.Collections;
using System.Text;

namespace FormatStudio.Expressions;

/// <summary>Resolves names during evaluation: fields of a struct, <c>_parent</c>, <c>_root</c>.</summary>
public interface IExprScope
{
    bool TryGet(string name, out object? value);
}

/// <summary>A variant value during evaluation, so that <c>tagof(x)</c> works.</summary>
public sealed record VariantValue(decimal Tag, string Case, object? Value);

/// <summary>
/// Evaluates expressions. Values are null, bool, decimal (integers and enum values), double, string, byte[],
/// lists, <see cref="IExprScope"/> (structs) and <see cref="VariantValue"/>.
/// </summary>
public sealed class ExprEvaluator(Func<string, string, decimal?> enumLookup)
{
    public object? Evaluate(Expr expression, IExprScope scope) => expression switch
    {
        LiteralExpr literal => literal.Value,
        NameExpr name => scope.TryGet(name.Name, out var value) ? value : throw new ExprException($"Unknown name '{name.Name}'"),
        EnumConstExpr constant => enumLookup(constant.Enum, constant.Member)
            ?? throw new ExprException($"Unknown enum constant {constant}"),
        MemberExpr member => Member(Evaluate(member.Target, scope), member.Name),
        IndexExpr index => Index(Evaluate(index.Target, scope), Evaluate(index.Index, scope)),
        UnaryExpr unary => Unary(unary.Operator, Evaluate(unary.Operand, scope)),
        BinaryExpr { Operator: "&&" } and => ToBool(Evaluate(and.Left, scope)) && ToBool(Evaluate(and.Right, scope)),
        BinaryExpr { Operator: "||" } or => ToBool(Evaluate(or.Left, scope)) || ToBool(Evaluate(or.Right, scope)),
        BinaryExpr binary => Binary(binary.Operator, Evaluate(binary.Left, scope), Evaluate(binary.Right, scope)),
        ConditionalExpr conditional => ToBool(Evaluate(conditional.Condition, scope))
            ? Evaluate(conditional.Then, scope)
            : Evaluate(conditional.Else, scope),
        CallExpr call => Call(call, scope),
        _ => throw new ExprException($"Can't evaluate {expression}"),
    };

    public bool EvaluateBool(Expr expression, IExprScope scope) => ToBool(Evaluate(expression, scope));

    public long EvaluateLong(Expr expression, IExprScope scope)
    {
        var value = ToNumber(Evaluate(expression, scope));
        if (value != decimal.Truncate(value)) throw new ExprException($"{expression} is not an integer ({value})");
        return (long)value;
    }

    private static object? Member(object? target, string name) => target switch
    {
        IExprScope scope => scope.TryGet(name, out var value) ? value : throw new ExprException($"No member '{name}'"),
        VariantValue variant => Member(variant.Value, name),
        null => throw new ExprException($"Can't read '{name}' of null"),
        _ => throw new ExprException($"Can't read '{name}' of a {Describe(target)}"),
    };

    private static object? Index(object? target, object? index)
    {
        var i = (int)ToNumber(index);
        return target switch
        {
            byte[] bytes when i >= 0 && i < bytes.Length => (decimal)bytes[i],
            IList list when i >= 0 && i < list.Count => list[i],
            byte[] or IList => throw new ExprException($"Index {i} is out of range"),
            _ => throw new ExprException($"Can't index a {Describe(target)}"),
        };
    }

    private object? Call(CallExpr call, IExprScope scope)
    {
        var arguments = call.Arguments.Select(a => Evaluate(a, scope)).ToList();
        if (arguments.Count != 1) throw new ExprException($"{call.Function}() takes one argument");
        var argument = arguments[0];
        return call.Function switch
        {
            "len" => argument switch
            {
                string s => s.Length,
                byte[] bytes => bytes.Length,
                ICollection collection => collection.Count,
                null => 0m,
                _ => throw new ExprException($"len() of a {Describe(argument)}"),
            },
            "bytelen" => argument switch
            {
                string s => Encoding.UTF8.GetByteCount(s),
                byte[] bytes => bytes.Length,
                _ => throw new ExprException($"bytelen() of a {Describe(argument)}"),
            },
            "tagof" => argument is VariantValue variant ? variant.Tag : throw new ExprException($"tagof() of a {Describe(argument)}"),
            _ => throw new ExprException($"Unknown function {call.Function}()"),
        };
    }

    private static object? Unary(string op, object? operand) => op switch
    {
        "!" => !ToBool(operand),
        "-" => operand is double d ? -d : -ToNumber(operand),
        _ => (decimal)~(long)ToNumber(operand),
    };

    private static object? Binary(string op, object? left, object? right)
    {
        switch (op)
        {
            case "==": return AreEqual(left, right);
            case "!=": return !AreEqual(left, right);
        }

        if (op == "+" && (left is string || right is string)) return $"{left}{right}";

        if (left is double || right is double)
        {
            var l = ToDouble(left);
            var r = ToDouble(right);
            return op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                "%" => l % r,
                "<" => l < r,
                "<=" => l <= r,
                ">" => l > r,
                ">=" => l >= r,
                _ => throw new ExprException($"Operator {op} needs integers"),
            };
        }

        var a = ToNumber(left);
        var b = ToNumber(right);
        return op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b == 0 ? throw new ExprException("Division by zero") : decimal.Truncate(a / b),
            "%" => b == 0 ? throw new ExprException("Division by zero") : a % b,
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            "&" => (decimal)((long)a & (long)b),
            "|" => (decimal)((long)a | (long)b),
            "^" => (decimal)((long)a ^ (long)b),
            "<<" => (decimal)((long)a << (int)b),
            ">>" => (decimal)((long)a >> (int)b),
            _ => throw new ExprException($"Unknown operator {op}"),
        };
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (IsNumber(left) && IsNumber(right))
            return left is double || right is double ? ToDouble(left) == ToDouble(right) : ToNumber(left) == ToNumber(right);
        return Equals(left, right);
    }

    private static bool IsNumber(object value) => value is decimal or double or bool;

    public static bool ToBool(object? value) => value switch
    {
        bool b => b,
        decimal d => d != 0,
        double d => d != 0,
        null => false,
        _ => throw new ExprException($"Expected a boolean, got a {Describe(value)}"),
    };

    public static decimal ToNumber(object? value) => value switch
    {
        decimal d => d,
        double d => (decimal)d,
        bool b => b ? 1 : 0,
        _ => throw new ExprException($"Expected a number, got a {Describe(value)}"),
    };

    private static double ToDouble(object? value) => value is double d ? d : (double)ToNumber(value);

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string => "string",
        byte[] => "byte array",
        IExprScope => "struct",
        VariantValue => "variant",
        IList => "list",
        _ => value.GetType().Name,
    };
}
