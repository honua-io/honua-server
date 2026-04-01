// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Evaluates a <see cref="FilterExpression"/> AST against an in-memory property dictionary.
/// Used for subscription-time filtering of CDC events. Static, allocation-free on comparison
/// paths, no reflection — safe for AOT and hot-path broadcast evaluation.
/// </summary>
public static class InMemoryFilterEvaluator
{
    /// <summary>
    /// Maximum expression depth allowed for streaming filters (DoS protection).
    /// </summary>
    public const int MaxStreamingDepth = 10;

    /// <summary>
    /// Evaluates a filter expression against a JSON-element property dictionary.
    /// Returns true if the properties satisfy the filter.
    /// </summary>
    public static bool Evaluate(FilterExpression expression, IReadOnlyDictionary<string, JsonElement> properties)
    {
        return EvaluateBool(expression, properties, depth: 0);
    }

    /// <summary>
    /// Validates that a filter expression only uses the subset of nodes and operators
    /// supported by streaming subscriptions.
    /// </summary>
    /// <param name="expression">Expression to validate.</param>
    /// <param name="error">Validation error when the expression is not stream-evaluable.</param>
    /// <returns><see langword="true"/> when the expression can be evaluated in-memory for streaming.</returns>
    public static bool TryValidateStreamingExpression(FilterExpression expression, out string? error)
    {
        return TryValidateBooleanExpression(expression, out error);
    }

    private static bool EvaluateBool(FilterExpression expr, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        if (depth > MaxStreamingDepth)
        {
            return false; // Fail closed for expressions that exceed streaming limits.
        }

        return expr switch
        {
            BinaryExpression bin => EvaluateBinary(bin, props, depth),
            UnaryExpression un => EvaluateUnary(un, props, depth),
            Literal lit => lit.Type == LiteralType.Boolean && lit.Value is true,
            _ => false
        };
    }

    private static bool EvaluateBinary(BinaryExpression bin, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        // Short-circuit logical operators.
        if (bin.Operator == BinaryOperator.And)
        {
            return EvaluateBool(bin.Left, props, depth + 1) && EvaluateBool(bin.Right, props, depth + 1);
        }

        if (bin.Operator == BinaryOperator.Or)
        {
            return EvaluateBool(bin.Left, props, depth + 1) || EvaluateBool(bin.Right, props, depth + 1);
        }

        // IN / NOT IN: left is property, right is ValueList.
        if (bin.Operator is BinaryOperator.In or BinaryOperator.NotIn)
        {
            return EvaluateIn(bin, props, depth);
        }

        // Comparison operators: resolve both sides to comparable values.
        var left = ResolveValue(bin.Left, props);
        var right = ResolveValue(bin.Right, props);

        // Null comparisons: only Equal/NotEqual are meaningful.
        if (left is null || right is null)
        {
            return bin.Operator switch
            {
                BinaryOperator.Equal => left is null && right is null,
                BinaryOperator.NotEqual => !(left is null && right is null),
                _ => false
            };
        }

        return bin.Operator switch
        {
            BinaryOperator.Equal => CompareValues(left, right) == 0,
            BinaryOperator.NotEqual => CompareValues(left, right) != 0,
            BinaryOperator.LessThan => CompareValues(left, right) < 0,
            BinaryOperator.LessThanOrEqual => CompareValues(left, right) <= 0,
            BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
            BinaryOperator.GreaterThanOrEqual => CompareValues(left, right) >= 0,
            BinaryOperator.Like => EvaluateLike(left, right),
            BinaryOperator.NotLike => !EvaluateLike(left, right),
            _ => false
        };
    }

    private static bool EvaluateUnary(UnaryExpression un, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        return un.Operator switch
        {
            UnaryOperator.Not => !EvaluateBool(un.Operand, props, depth + 1),
            UnaryOperator.IsNull => ResolveValue(un.Operand, props) is null,
            UnaryOperator.IsNotNull => ResolveValue(un.Operand, props) is not null,
            _ => false
        };
    }

    private static bool EvaluateIn(BinaryExpression bin, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        var left = ResolveValue(bin.Left, props);
        if (left is null)
        {
            return false;
        }

        if (bin.Right is not ValueList valueList)
        {
            return false;
        }

        foreach (var item in valueList.Values)
        {
            var itemValue = ResolveValue(item, props);
            if (itemValue is not null && CompareValues(left, itemValue) == 0)
            {
                return bin.Operator == BinaryOperator.In;
            }
        }

        return bin.Operator == BinaryOperator.NotIn;
    }

    private static object? ResolveValue(FilterExpression expr, IReadOnlyDictionary<string, JsonElement> props)
    {
        return expr switch
        {
            PropertyReference prop => ResolveProperty(prop.PropertyName, props),
            Literal lit => ResolveLiteral(lit),
            _ => null
        };
    }

    private static object? ResolveProperty(string name, IReadOnlyDictionary<string, JsonElement> props)
    {
        if (!props.TryGetValue(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ResolveNumericElement(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static object? ResolveLiteral(Literal lit)
    {
        return lit.Type switch
        {
            LiteralType.Null => null,
            LiteralType.Text => lit.Value as string,
            LiteralType.Number => lit.Value switch
            {
                byte b => (long)b,
                sbyte sb => (long)sb,
                short s => (long)s,
                ushort us => (long)us,
                int i => (long)i,
                uint ui => (long)ui,
                long l => l,
                decimal m => m,
                float f => (double)f,
                double d => d,
                _ => Convert.ToDouble(lit.Value, CultureInfo.InvariantCulture)
            },
            LiteralType.Boolean => lit.Value is true,
            LiteralType.Date or LiteralType.DateTime => lit.Value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
                string s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : (object?)s,
                _ => lit.Value
            },
            _ => lit.Value
        };
    }

    private static int CompareValues(object left, object right)
    {
        if (TryGetInt64(left, out var li) && TryGetInt64(right, out var ri))
        {
            return li.CompareTo(ri);
        }

        if (TryGetDecimal(left, out var ldec) && TryGetDecimal(right, out var rdec))
        {
            return ldec.CompareTo(rdec);
        }

        // Numeric comparison.
        if (TryGetDouble(left, out var ld) && TryGetDouble(right, out var rd))
        {
            return ld.CompareTo(rd);
        }

        // DateTimeOffset comparison.
        if (left is DateTimeOffset ldt && right is DateTimeOffset rdt)
        {
            return ldt.CompareTo(rdt);
        }

        // Boolean comparison.
        if (left is bool lb && right is bool rb)
        {
            return lb.CompareTo(rb);
        }

        // Match the default SQL/CQL semantics used by the main query path.
        var ls = left.ToString() ?? string.Empty;
        var rs = right.ToString() ?? string.Empty;
        return string.Compare(ls, rs, StringComparison.Ordinal);
    }

    private static bool TryGetDouble(object value, out double result)
    {
        if (value is double d) { result = d; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is float f) { result = f; return true; }
        if (value is decimal m) { result = (double)m; return true; }
        result = 0;
        return false;
    }

    private static bool TryGetInt64(object value, out long result)
    {
        switch (value)
        {
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case int i:
                result = i;
                return true;
            case uint ui:
                result = (long)ui;
                return true;
            case long l:
                result = l;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case int i:
                result = i;
                return true;
            case uint ui:
                result = ui;
                return true;
            case long l:
                result = l;
                return true;
            case decimal m:
                result = m;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static object ResolveNumericElement(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            return integer;
        }

        if (element.TryGetDecimal(out var preciseDecimal))
        {
            return preciseDecimal;
        }

        return element.GetDouble();
    }

    private static bool TryValidateBooleanExpression(FilterExpression expression, out string? error)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                return TryValidateBinaryExpression(binary, out error);
            case UnaryExpression unary:
                return TryValidateUnaryExpression(unary, out error);
            case Literal literal when literal.Type == LiteralType.Boolean:
                error = null;
                return true;
            default:
                return FailUnsupportedBooleanExpression(expression, out error);
        }
    }

    private static bool TryValidateBinaryExpression(BinaryExpression binary, out string? error)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
            case BinaryOperator.Or:
                return TryValidateBooleanExpression(binary.Left, out error) &&
                       TryValidateBooleanExpression(binary.Right, out error);
            case BinaryOperator.Equal:
            case BinaryOperator.NotEqual:
            case BinaryOperator.LessThan:
            case BinaryOperator.LessThanOrEqual:
            case BinaryOperator.GreaterThan:
            case BinaryOperator.GreaterThanOrEqual:
            case BinaryOperator.Like:
            case BinaryOperator.NotLike:
                return TryValidateScalarExpression(binary.Left, out error) &&
                       TryValidateScalarExpression(binary.Right, out error);
            case BinaryOperator.In:
            case BinaryOperator.NotIn:
                return TryValidateScalarExpression(binary.Left, out error) &&
                       TryValidateValueList(binary.Right, out error);
            default:
                error = $"Streaming subscriptions do not support the '{binary.Operator}' operator in filter expressions.";
                return false;
        }
    }

    private static bool TryValidateUnaryExpression(UnaryExpression unary, out string? error)
    {
        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                return TryValidateBooleanExpression(unary.Operand, out error);
            case UnaryOperator.IsNull:
            case UnaryOperator.IsNotNull:
                return TryValidateScalarExpression(unary.Operand, out error);
            default:
                error = $"Streaming subscriptions do not support the '{unary.Operator}' operator in filter expressions.";
                return false;
        }
    }

    private static bool TryValidateScalarExpression(FilterExpression expression, out string? error)
    {
        switch (expression)
        {
            case PropertyReference:
            case Literal:
                error = null;
                return true;
            case FunctionCall:
                error = "Streaming subscriptions do not support function calls in filter expressions.";
                return false;
            case SpatialPredicate or SpatialDistancePredicate:
                error = "Streaming subscriptions do not support spatial predicates inside filter expressions. Use the bbox parameter instead.";
                return false;
            case TemporalPredicate:
                error = "Streaming subscriptions do not support temporal predicates in filter expressions.";
                return false;
            case ValueList:
                error = "Streaming subscriptions do not support nested value lists in filter expressions.";
                return false;
            default:
                error = $"Streaming subscriptions do not support {expression.GetType().Name} expressions.";
                return false;
        }
    }

    private static bool TryValidateValueList(FilterExpression expression, out string? error)
    {
        if (expression is not ValueList valueList)
        {
            error = "Streaming subscriptions require a literal value list for IN/NOT IN filters.";
            return false;
        }

        foreach (var value in valueList.Values)
        {
            if (!TryValidateScalarExpression(value, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool FailUnsupportedBooleanExpression(FilterExpression expression, out string? error)
    {
        error = expression switch
        {
            FunctionCall => "Streaming subscriptions do not support function calls in filter expressions.",
            SpatialPredicate or SpatialDistancePredicate => "Streaming subscriptions do not support spatial predicates inside filter expressions. Use the bbox parameter instead.",
            TemporalPredicate => "Streaming subscriptions do not support temporal predicates in filter expressions.",
            _ => $"Streaming subscriptions do not support {expression.GetType().Name} expressions."
        };
        return false;
    }

    // Unbounded by design: entries are created per unique LIKE pattern at subscription time
    // (not per event), so growth is bounded by the number of distinct streaming filter patterns.
    private static readonly ConcurrentDictionary<string, Regex> LikePatternCache = new();

    private static bool EvaluateLike(object left, object right)
    {
        var input = left.ToString() ?? string.Empty;
        var pattern = right.ToString() ?? string.Empty;

        // Cache compiled regex per LIKE pattern to avoid per-event allocation on the broadcast hot path.
        var regex = LikePatternCache.GetOrAdd(pattern, static p =>
        {
            // Convert SQL LIKE pattern to regex: % → .*, _ → ., escape others.
            var regexPattern = "^" + Regex.Escape(p)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";
            return new Regex(regexPattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        });

        return regex.IsMatch(input);
    }

    /// <summary>
    /// Validates that the expression depth does not exceed the streaming limit.
    /// </summary>
    public static bool ExceedsMaxDepth(FilterExpression expression)
    {
        return MeasureDepth(expression, 0) > MaxStreamingDepth;
    }

    private static int MeasureDepth(FilterExpression expr, int current)
    {
        if (current > MaxStreamingDepth)
        {
            return current;
        }

        return expr switch
        {
            BinaryExpression bin => Math.Max(
                MeasureDepth(bin.Left, current + 1),
                MeasureDepth(bin.Right, current + 1)),
            UnaryExpression un => MeasureDepth(un.Operand, current + 1),
            SpatialPredicate spatial => Math.Max(
                MeasureDepth(spatial.Left, current + 1),
                MeasureDepth(spatial.Right, current + 1)),
            SpatialDistancePredicate spatialDistance => Math.Max(
                MeasureDepth(spatialDistance.Left, current + 1),
                Math.Max(
                    MeasureDepth(spatialDistance.Right, current + 1),
                    MeasureDepth(spatialDistance.Distance, current + 1))),
            TemporalPredicate temporal => Math.Max(
                MeasureDepth(temporal.Left, current + 1),
                MeasureDepth(temporal.Right, current + 1)),
            ArrayPredicate array => Math.Max(
                MeasureDepth(array.Left, current + 1),
                MeasureDepth(array.Right, current + 1)),
            FunctionCall function => MeasureDepth(function.Arguments, current + 1),
            ArrayLiteral arrayLiteral => MeasureDepth(arrayLiteral.Elements, current + 1),
            ValueList valueList => MeasureDepth(valueList.Values, current + 1),
            IntervalLiteral interval => Math.Max(
                interval.Start is null ? current : MeasureDepth(interval.Start, current + 1),
                interval.End is null ? current : MeasureDepth(interval.End, current + 1)),
            _ => current
        };
    }

    private static int MeasureDepth(IReadOnlyList<FilterExpression> expressions, int current)
    {
        var max = current;
        foreach (var expression in expressions)
        {
            max = Math.Max(max, MeasureDepth(expression, current));
        }

        return max;
    }
}
