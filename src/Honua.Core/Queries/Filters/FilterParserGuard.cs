// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Shared guardrails for filter-parser hostile-input handling.
/// </summary>
/// <remarks>
/// Geometry-shaped guard methods (WKT / GeoJSON parsing, coordinate caps,
/// text-size caps applied to geometry payloads) live in
/// <c>FilterParserGeometryGuard</c> inside <c>Honua.Geometry</c>; they pull
/// NetTopologySuite, which <c>Honua.Core</c> no longer references directly.
/// </remarks>
internal static class FilterParserGuard
{
    internal const int MaxExpressionDepth = FilterExpressionNormalizer.MaxExpressionDepth;
    internal const int MaxGeometryTextBytes = 5 * 1024 * 1024;
    internal const int MaxGeometryVertices = 50_000;

    // Caps applied to un-structured parser input so that a single oversized token
    // cannot pin down the heap or slow down comparison loops.
    internal const int MaxStringLiteralLength = 64 * 1024;       // 64 KiB per literal
    internal const int MaxIdentifierLength = 256;                // conservative for all backends

    // SQL Server rejects a statement above 2100 parameters. objectIds above 2000 are
    // bound as one JSON parameter; a where IN-list is one parameter per value, so the
    // same 2000 budget is a client error here instead of a provider 500. The leftover
    // parameters cover paging and the rest of the statement.
    internal const int MaxInListSize = 2_000;

    // Cap on the size of a parsed expression tree, counted the way recursive walks visit
    // it (a subtree shared by several parents counts once per reference). Ten IN-list
    // budgets; applies to parser output only, never to server-built expressions.
    // IN values themselves share MaxInListSize across the whole expression.
    internal const int MaxExpressionNodes = 10 * MaxInListSize;

    public static void EnsureExpressionDepth(int depth)
    {
        if (depth > MaxExpressionDepth)
        {
            throw new ArgumentException($"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
        }
    }

    // A flat AND/OR sequence folds into a left-deep chain whose depth equals its operand
    // count, so a sequence longer than MaxExpressionDepth can never pass the tree check
    // below. Rejecting it while the parser is still folding keeps the produced tree small.
    public static void EnsureLogicalOperandCount(int count)
    {
        if (count > MaxExpressionDepth)
        {
            throw new ArgumentException(
                $"Filter expression combines more than {MaxExpressionDepth} operands in one AND/OR sequence, " +
                $"which exceeds the maximum nesting depth of {MaxExpressionDepth}.");
        }
    }

    // Applied by every parser to the tree it produced, before returning it: parser-side
    // depth counters track syntactic nesting only, while folded operator sequences
    // (logical, arithmetic, concatenation) deepen the tree without nesting the input.
    public static void EnsureExpressionTree(FilterExpression expression)
    {
        FilterExpressionNormalizer.EnsureWithinBounds(expression, MaxExpressionNodes);
        EnsureAggregateInListValues(expression);
    }

    // Each IN/NOT IN value is its own SQL parameter. A single list is already capped;
    // several lists in one expression share that budget so the statement stays under
    // the strictest provider parameter limit.
    private static void EnsureAggregateInListValues(FilterExpression expression)
    {
        var total = 0;
        var stack = new Stack<FilterExpression>();
        stack.Push(expression);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is BinaryExpression
                {
                    Operator: BinaryOperator.In or BinaryOperator.NotIn,
                    Right: ValueList values
                })
            {
                total += values.Values.Count;
                if (total > MaxInListSize)
                {
                    throw new ArgumentException(
                        $"Filter IN-lists exceed the maximum of {MaxInListSize} values.");
                }
            }

            foreach (var child in FilterExpressionNormalizer.EnumerateChildren(current))
            {
                stack.Push(child);
            }
        }
    }

    // Rejects NaN and ±Infinity from numeric literals. `double.TryParse(... Float ...)`
    // silently returns Infinity for exponent overflow (e.g. "1e999") and accepts the
    // literals "NaN"/"Infinity" by default on most culture settings — both of which
    // produce nonsense downstream when used in comparisons or geometry coords.
    public static void EnsureFiniteNumber(double value, string description)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                $"{description} must be a finite number (received {value}).");
        }
    }

    public static void EnsureStringLiteralLength(int length, string description)
    {
        if (length > MaxStringLiteralLength)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum string literal length of {MaxStringLiteralLength} characters.");
        }
    }

    public static void EnsureIdentifierLength(int length, string description)
    {
        if (length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum identifier length of {MaxIdentifierLength} characters.");
        }
    }

    public static void EnsureInListSize(int count, string description)
    {
        if (count > MaxInListSize)
        {
            throw new ArgumentException(
                $"{description} IN-list exceeds the maximum of {MaxInListSize} values.");
        }
    }

    // Function-call argument lists and array literals are flat operand lists like an
    // IN-list, so they share its cap; checked while the parser materializes the list.
    public static void EnsureValueListSize(int count, string description)
    {
        if (count > MaxInListSize)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum of {MaxInListSize} values.");
        }
    }

}
