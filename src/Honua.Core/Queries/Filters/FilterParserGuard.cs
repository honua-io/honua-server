// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    internal const int MaxInListSize = 10_000;                   // 10k elements per IN(...) list

    // Cap on the size of a parsed expression tree, counted the way recursive walks visit
    // it (a subtree shared by several parents counts once per reference). Ten full
    // IN-lists; applies to parser output only, never to server-built expressions.
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
        => FilterExpressionNormalizer.EnsureWithinBounds(expression, MaxExpressionNodes);

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

}
