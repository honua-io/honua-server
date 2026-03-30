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

    private static bool EvaluateBool(FilterExpression expr, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        if (depth > MaxStreamingDepth)
        {
            return true; // Safety: pass through overly complex filters.
        }

        return expr switch
        {
            BinaryExpression bin => EvaluateBinary(bin, props, depth),
            UnaryExpression un => EvaluateUnary(un, props, depth),
            Literal lit => lit.Type == LiteralType.Boolean && lit.Value is true,
            _ => true // Unsupported node types pass through.
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
            _ => true // Unsupported operator — pass through.
        };
    }

    private static bool EvaluateUnary(UnaryExpression un, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        return un.Operator switch
        {
            UnaryOperator.Not => !EvaluateBool(un.Operand, props, depth + 1),
            UnaryOperator.IsNull => ResolveValue(un.Operand, props) is null,
            UnaryOperator.IsNotNull => ResolveValue(un.Operand, props) is not null,
            _ => true
        };
    }

    private static bool EvaluateIn(BinaryExpression bin, IReadOnlyDictionary<string, JsonElement> props, int depth)
    {
        var left = ResolveValue(bin.Left, props);
        if (left is null)
        {
            return bin.Operator == BinaryOperator.NotIn;
        }

        if (bin.Right is not ValueList valueList)
        {
            return bin.Operator == BinaryOperator.NotIn;
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
            JsonValueKind.Number => element.GetDouble(),
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
                double d => d,
                int i => (double)i,
                long l => (double)l,
                float f => (double)f,
                decimal m => (double)m,
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

        // String comparison (case-insensitive for filter evaluation).
        var ls = left.ToString() ?? string.Empty;
        var rs = right.ToString() ?? string.Empty;
        return string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);
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
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
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
            _ => current
        };
    }
}
