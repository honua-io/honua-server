// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution.Expressions;

/// <summary>
/// Base of the immutable expression AST. Every node is a hand-written, sealed
/// evaluator — there is no reflection, no dynamic dispatch beyond the virtual
/// <see cref="Evaluate"/>, and no code generation, so the tree is fully AOT-safe.
/// Values flow as <see cref="object"/> boxes constrained to the runtime value set
/// produced by the grammar: <c>double</c>, <c>string</c>, <c>bool</c>,
/// <c>DateTimeOffset</c>, or <c>null</c>.
/// </summary>
internal abstract class ExpressionNode
{
    /// <summary>Evaluates this node against the supplied field-reference context.</summary>
    public abstract object? Evaluate(IExpressionContext context);
}

/// <summary>A literal constant (number, string, bool, or null).</summary>
internal sealed class LiteralNode(object? value) : ExpressionNode
{
    public object? Value { get; } = value;

    public override object? Evaluate(IExpressionContext context) => Value;
}

/// <summary>A bare identifier resolved as a field reference; missing fields are <c>null</c>.</summary>
internal sealed class FieldNode(string name) : ExpressionNode
{
    public string Name { get; } = name;

    public override object? Evaluate(IExpressionContext context)
        => context.TryGetField(Name, out var value) ? value : null;
}

/// <summary>A prefix unary operation: numeric negation or logical not.</summary>
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

/// <summary>A binary arithmetic / comparison / logical operation.</summary>
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

/// <summary>A ternary conditional <c>cond ? whenTrue : whenFalse</c>.</summary>
internal sealed class ConditionalNode(ExpressionNode condition, ExpressionNode whenTrue, ExpressionNode whenFalse) : ExpressionNode
{
    public override object? Evaluate(IExpressionContext context)
        => ExpressionValues.ToBool(condition.Evaluate(context))
            ? whenTrue.Evaluate(context)
            : whenFalse.Evaluate(context);
}

/// <summary>A call to a whitelisted function with already-parsed argument nodes.</summary>
internal sealed class FunctionNode(string name, IReadOnlyList<ExpressionNode> arguments) : ExpressionNode
{
    public string Name { get; } = name;

    public IReadOnlyList<ExpressionNode> Arguments { get; } = arguments;

    public override object? Evaluate(IExpressionContext context)
        => ExpressionFunctions.Invoke(Name, Arguments, context);
}

/// <summary>
/// Shared value coercion helpers used by the evaluator nodes. Centralizes the
/// limited, well-defined coercion rules so behavior is consistent across operators
/// and functions.
/// </summary>
internal static class ExpressionValues
{
    /// <summary>
    /// Tolerance used wherever a computed <c>double</c> is compared against the literal
    /// zero sentinel (divide/modulo-by-zero guards, truthy coercion). A value derived
    /// through a chain of arithmetic that mathematically cancels to zero can round to a
    /// tiny non-zero residue instead of exact <c>0.0</c>; comparing with exact equality
    /// would let that residue silently produce a huge/garbage result (division) or the
    /// wrong branch (truthiness) instead of the intended zero-sentinel behavior.
    /// </summary>
    internal const double ZeroTolerance = 1e-9;

    /// <summary>Coerces a value to a <c>double</c>, throwing for non-numeric input.</summary>
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

    /// <summary>
    /// Attempts numeric coercion without throwing. Handles every boxed numeric the
    /// feature attribute table can hold (NetTopologySuite stores integers as boxed
    /// <c>int</c>/<c>long</c>), plus bool, parseable strings, and dates.
    /// </summary>
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

    /// <summary>Coerces a value to a truth value (null and 0 are false; empty string is false).</summary>
    public static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => Math.Abs(d) >= ZeroTolerance,
        string s => s.Length > 0 && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) && s != "0",
        _ => true,
    };

    /// <summary>Equality with numeric promotion; otherwise invariant string comparison.</summary>
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
            // codeql[cs/equality-on-floats] -- exact comparison is required for this sentinel, encoding, or same-source value.
            return ln == rn;
        }

        return string.Equals(
            ExpressionEngine.ToInvariantString(left),
            ExpressionEngine.ToInvariantString(right),
            StringComparison.Ordinal);
    }

    /// <summary>Ordering comparison: numeric when both coerce, otherwise ordinal string compare.</summary>
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
