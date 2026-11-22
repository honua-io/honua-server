// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Queries.Filters;

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
        EnsureSemanticGuards(expression);
    }

    internal static void EnsureCompatibleLiteralComparison(FilterExpression left, FilterExpression right)
    {
        if (left is not Literal leftLiteral || right is not Literal rightLiteral)
        {
            return;
        }

        if (leftLiteral.Type == LiteralType.Null || rightLiteral.Type == LiteralType.Null)
        {
            return;
        }

        var compatible = leftLiteral.Type == rightLiteral.Type
            || (leftLiteral.Type is LiteralType.Date or LiteralType.DateTime
                && rightLiteral.Type is LiteralType.Date or LiteralType.DateTime);
        if (!compatible)
        {
            throw new ArgumentException(
                $"Incompatible literal types in comparison: {leftLiteral.Type} and {rightLiteral.Type}.");
        }
    }

    private static void EnsureSemanticGuards(FilterExpression expression)
    {
        var stack = new Stack<FilterExpression>();
        stack.Push(expression);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            ValidateExpressionSemantics(current);

            foreach (var child in FilterExpressionNormalizer.EnumerateChildren(current))
            {
                stack.Push(child);
            }
        }
    }

    private static void ValidateExpressionSemantics(FilterExpression expression)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
            {
                EnsureCompatibleLiteralComparison(binary.Left, binary.Right);
            }

            if (binary.Operator is BinaryOperator.Divide or BinaryOperator.Div or BinaryOperator.Modulo
                && IsConstantZero(binary.Right))
            {
                throw new ArgumentException("Division by zero is not allowed in filter expressions.");
            }
        }
        else if (expression is FunctionCall { Arguments.Count: 2 } function
            && function.FunctionName.Equals("MOD", StringComparison.OrdinalIgnoreCase)
            && IsConstantZero(function.Arguments[1]))
        {
            throw new ArgumentException("Division by zero is not allowed in filter expressions.");
        }
    }

    private static bool IsConstantZero(FilterExpression expression)
        => TryEvaluateConstantNumber(expression, out var value) && value == 0d;

    private static bool TryEvaluateConstantNumber(FilterExpression expression, out double value)
    {
        switch (expression)
        {
            case Literal { Type: LiteralType.Number, Value: not null } literal:
                value = Convert.ToDouble(literal.Value, CultureInfo.InvariantCulture);
                return double.IsFinite(value);
            case UnaryExpression { Operator: UnaryOperator.Negate } unary
                when TryEvaluateConstantNumber(unary.Operand, out var inner):
                value = -inner;
                return true;
            case BinaryExpression binary
                when TryEvaluateConstantNumber(binary.Left, out var left)
                     && TryEvaluateConstantNumber(binary.Right, out var right):
                return TryEvaluateConstantBinary(binary.Operator, left, right, out value);
            default:
                value = 0d;
                return false;
        }
    }

    private static bool TryEvaluateConstantBinary(BinaryOperator op, double left, double right, out double value)
    {
        switch (op)
        {
            case BinaryOperator.Add:
                value = left + right;
                return double.IsFinite(value);
            case BinaryOperator.Subtract:
                value = left - right;
                return double.IsFinite(value);
            case BinaryOperator.Multiply:
                value = left * right;
                return double.IsFinite(value);
            case BinaryOperator.Divide:
            case BinaryOperator.Div:
            case BinaryOperator.Modulo:
                if (right == 0d)
                {
                    throw new ArgumentException("Division by zero is not allowed in filter expressions.");
                }

                value = op switch
                {
                    BinaryOperator.Divide => left / right,
                    BinaryOperator.Div => Math.Truncate(left / right),
                    _ => left % right
                };
                return double.IsFinite(value);
            case BinaryOperator.Power:
                value = Math.Pow(left, right);
                return double.IsFinite(value);
            default:
                value = 0d;
                return false;
        }
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
