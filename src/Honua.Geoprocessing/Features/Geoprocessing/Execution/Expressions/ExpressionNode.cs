// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution.Expressions;

internal abstract class ExpressionNode
{
    public abstract object? Evaluate(IExpressionContext context);
}

internal sealed class LiteralNode(object? value) : ExpressionNode
{
    public object? Value { get; } = value;

    public override object? Evaluate(IExpressionContext context) => Value;
}

internal sealed class FieldNode(string name) : ExpressionNode
{
    public string Name { get; } = name;

    public override object? Evaluate(IExpressionContext context)
        => context.TryGetField(Name, out var value) ? value : null;
}

internal sealed class UnaryNode(ExpressionTokenKind op, ExpressionNode operand) : ExpressionNode
{
    public override object? Evaluate(IExpressionContext context)
    {
        var value = operand.Evaluate(context);
        return op switch
        {
            ExpressionTokenKind.Minus => -ExpressionValues.ToNumber(value),
            ExpressionTokenKind.Not => !ExpressionValues.ToBool(value),
            _ => throw new ExpressionEvaluationException($"unsupported unary operator '{op}'"),
        };
    }
}

internal sealed class BinaryNode(ExpressionTokenKind op, ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    public override object? Evaluate(IExpressionContext context)
    {
        // Short-circuiting logical operators.
        switch (op)
        {
            case ExpressionTokenKind.AndAnd:
                return ExpressionValues.ToBool(left.Evaluate(context)) && ExpressionValues.ToBool(right.Evaluate(context));
            case ExpressionTokenKind.OrOr:
                return ExpressionValues.ToBool(left.Evaluate(context)) || ExpressionValues.ToBool(right.Evaluate(context));
        }

        var l = left.Evaluate(context);
        var r = right.Evaluate(context);

        switch (op)
        {
            case ExpressionTokenKind.Plus:
                // '+' is overloaded: string concatenation when either side is a
                // string, numeric addition otherwise.
                if (l is string || r is string)
                {
                    return ExpressionEngine.ToInvariantString(l) + ExpressionEngine.ToInvariantString(r);
                }

                return ExpressionValues.ToNumber(l) + ExpressionValues.ToNumber(r);
            case ExpressionTokenKind.Minus:
                return ExpressionValues.ToNumber(l) - ExpressionValues.ToNumber(r);
            case ExpressionTokenKind.Star:
                return ExpressionValues.ToNumber(l) * ExpressionValues.ToNumber(r);
            case ExpressionTokenKind.Slash:
                {
                    var divisor = ExpressionValues.ToNumber(r);
                    if (Math.Abs(divisor) < ExpressionValues.ZeroTolerance)
                    {
                        throw new ExpressionEvaluationException("division by zero");
                    }

                    return ExpressionValues.ToNumber(l) / divisor;
                }
            case ExpressionTokenKind.Percent:
                {
                    var divisor = ExpressionValues.ToNumber(r);
                    if (Math.Abs(divisor) < ExpressionValues.ZeroTolerance)
                    {
                        throw new ExpressionEvaluationException("modulo by zero");
                    }

                    return ExpressionValues.ToNumber(l) % divisor;
                }
            case ExpressionTokenKind.EqualEqual:
                return ExpressionValues.AreEqual(l, r);
            case ExpressionTokenKind.NotEqual:
                return !ExpressionValues.AreEqual(l, r);
            case ExpressionTokenKind.LessThan:
                return ExpressionValues.Compare(l, r) < 0;
            case ExpressionTokenKind.LessEqual:
                return ExpressionValues.Compare(l, r) <= 0;
            case ExpressionTokenKind.GreaterThan:
                return ExpressionValues.Compare(l, r) > 0;
            case ExpressionTokenKind.GreaterEqual:
                return ExpressionValues.Compare(l, r) >= 0;
            default:
                throw new ExpressionEvaluationException($"unsupported binary operator '{op}'");
        }
    }
}

internal sealed class ConditionalNode(ExpressionNode condition, ExpressionNode whenTrue, ExpressionNode whenFalse) : ExpressionNode
{
    public override object? Evaluate(IExpressionContext context)
        => ExpressionValues.ToBool(condition.Evaluate(context))
            ? whenTrue.Evaluate(context)
            : whenFalse.Evaluate(context);
}

internal sealed class FunctionNode(string name, IReadOnlyList<ExpressionNode> arguments) : ExpressionNode
{
    public string Name { get; } = name;

    public IReadOnlyList<ExpressionNode> Arguments { get; } = arguments;

    public override object? Evaluate(IExpressionContext context)
        => ExpressionFunctions.Invoke(Name, Arguments, context);
}

internal static class ExpressionValues
{
    // Arithmetic that cancels to zero can leave a floating-point residue, so exact equality would miss the zero sentinel and produce a garbage quotient or the wrong branch.
    internal const double ZeroTolerance = 1e-9;

    public static double ToNumber(object? value)
    {
        if (TryToNumber(value, out var number))
        {
            return number;
        }

        return value switch
        {
            null => throw new ExpressionEvaluationException("cannot use null in an arithmetic/numeric context"),
            string s => throw new ExpressionEvaluationException($"value '{s}' is not numeric"),
            _ => throw new ExpressionEvaluationException($"value of type {value.GetType().Name} is not numeric"),
        };
    }

    public static bool TryToNumber(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short sh: result = sh; return true;
            case byte by: result = by; return true;
            case decimal m: result = (double)m; return true;
            case bool b: result = b ? 1d : 0d; return true;
            case DateTimeOffset dto: result = dto.ToUnixTimeMilliseconds(); return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed; return true;
            default:
                result = 0d; return false;
        }
    }

    public static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => Math.Abs(d) >= ZeroTolerance,
        string s => s.Length > 0 && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) && s != "0",
        _ => true,
    };

    public static bool AreEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (TryToNumber(left, out var ln) && TryToNumber(right, out var rn))
        {
            // Intentional exact equality: this backs the expression language's `==`/`!=`
            // operators, which follow the same exact-numeric-equality semantics as the
            // CQL2/OData `=` filter operators elsewhere in this codebase (pushed down to
            // SQL `=`, see Cql2Parser/ODataFilterParser) rather than a tolerance compare.
            // A tolerance here would make identical-looking field values compare unequal
            // to their own literal, which is the opposite of what "==" means to a caller.
            return ln <= rn && ln >= rn;
        }

        return string.Equals(
            ExpressionEngine.ToInvariantString(left),
            ExpressionEngine.ToInvariantString(right),
            StringComparison.Ordinal);
    }

    public static int Compare(object? left, object? right)
    {
        if (TryToNumber(left, out var ln) && TryToNumber(right, out var rn))
        {
            return ln.CompareTo(rn);
        }

        return string.CompareOrdinal(
            ExpressionEngine.ToInvariantString(left),
            ExpressionEngine.ToInvariantString(right));
    }
}
