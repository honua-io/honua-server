// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        var leftType = InferStaticExpressionType(left);
        var rightType = InferStaticExpressionType(right);
        if (leftType is null || rightType is null)
        {
            return;
        }

        var compatible = leftType == rightType
            || (leftType is LiteralType.Date or LiteralType.DateTime
                && rightType is LiteralType.Date or LiteralType.DateTime);
        if (!compatible)
        {
            throw new ArgumentException(
                $"Incompatible literal types in comparison: {leftType} and {rightType}.");
        }
    }

    internal static LiteralType? InferStaticExpressionType(FilterExpression expression)
        => expression switch
        {
            Literal { Type: LiteralType.Null } => null,
            Literal literal => literal.Type,
            BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
                or BinaryOperator.Divide or BinaryOperator.Modulo or BinaryOperator.Div or BinaryOperator.Power }
                => LiteralType.Number,
            BinaryExpression => LiteralType.Boolean,
            UnaryExpression { Operator: UnaryOperator.Negate } => LiteralType.Number,
            UnaryExpression => LiteralType.Boolean,
            SpatialPredicate or SpatialDistancePredicate or TemporalPredicate or ArrayPredicate => LiteralType.Boolean,
            FunctionCall function => InferStaticFunctionType(function.FunctionName),
            _ => null
        };

    private static LiteralType? InferStaticFunctionType(string functionName)
        => functionName.ToUpperInvariant() switch
        {
            "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM" or "SUBSTRING" or "SUBSTR"
                or "REPLACE" or "CONCAT" or "LEFT" or "RIGHT" or "CASEI" or "ACCENTI"
                or "ST_GEOMETRYTYPE" => LiteralType.Text,
            "LENGTH" or "LEN" or "CHAR_LENGTH" or "CHARACTER_LENGTH" or "POSITION"
                or "ABS" or "CEIL" or "CEILING" or "FLOOR" or "ROUND" or "SIGN"
                or "POWER" or "MOD" or "SQRT" or "SIN" or "COS" or "TAN" or "LOG" or "EXP"
                or "COUNT" or "SUM" or "AVG" or "ST_AREA" or "ST_LENGTH" or "ST_PERIMETER"
                or "ST_DISTANCE" or "ST_NUMGEOMETRIES" or "ST_SRID" or "ST_X" or "ST_Y"
                or "GEODISTANCE" or "GEOLENGTH" or "YEAR" or "MONTH" or "DAY"
                or "HOUR" or "MINUTE" or "SECOND" or "FRACTIONALSECONDS" or "TOTALOFFSETMINUTES"
                or "EXTRACT_DOW" or "EXTRACT_DOY" or "EXTRACT_QUARTER" or "EXTRACT_WEEK"
                or "EXTRACT_EPOCH" => LiteralType.Number,
            "NOW" or "CURRENT_TIMESTAMP" or "MINDATETIME" or "MAXDATETIME" => LiteralType.DateTime,
            "CURRENT_DATE" or "DATE" => LiteralType.Date,
            _ => null
        };

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
        => EvaluateConstantDecimal(expression) == 0m;

    private static decimal? EvaluateConstantDecimal(FilterExpression expression)
    {
        try
        {
            return expression switch
            {
                Literal { Type: LiteralType.Number } literal => TryLiteralToExactDecimal(literal.Value),
                UnaryExpression { Operator: UnaryOperator.Negate } unary =>
                    EvaluateConstantDecimal(unary.Operand) is { } inner ? checked(-inner) : null,
                BinaryExpression binary when EvaluateConstantDecimal(binary.Left) is { } left
                    && EvaluateConstantDecimal(binary.Right) is { } right =>
                    EvaluateConstantBinary(binary.Operator, left, right),
                FunctionCall function => EvaluateConstantFunction(function),
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static decimal? TryLiteralToExactDecimal(object? value)
        => value switch
        {
            int i => i,
            long l => l,
            decimal m => m,
            double d when d == 0d => 0m,
            float f when f == 0f => 0m,
            _ => null
        };

    private static decimal? EvaluateConstantFunction(FunctionCall function)
    {
        var name = function.FunctionName.ToUpperInvariant();
        if (name == "COALESCE")
        {
            return EvaluateCoalesceDecimal(function.Arguments);
        }

        if (function.Arguments.Count == 1 && EvaluateConstantDecimal(function.Arguments[0]) is { } single)
        {
            return name switch
            {
                "ABS" => Math.Abs(single),
                "FLOOR" => Math.Floor(single),
                "CEIL" or "CEILING" => Math.Ceiling(single),
                "ROUND" => Math.Round(single, MidpointRounding.AwayFromZero),
                "SIGN" => Math.Sign(single),
                _ => null
            };
        }

        if (function.Arguments.Count == 2
            && EvaluateConstantDecimal(function.Arguments[0]) is { } first
            && EvaluateConstantDecimal(function.Arguments[1]) is { } second)
        {
            return name switch
            {
                "MOD" => second == 0m
                    ? throw new ArgumentException("Division by zero is not allowed in filter expressions.")
                    : first % second,
                "NULLIF" => first == second ? null : first,
                _ => null
            };
        }

        return null;
    }

    private static decimal? EvaluateCoalesceDecimal(IReadOnlyList<FilterExpression> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is Literal { Type: LiteralType.Null })
            {
                continue;
            }

            return EvaluateConstantDecimal(arguments[i]);
        }

        return null;
    }

    private static decimal? EvaluateConstantBinary(BinaryOperator op, decimal left, decimal right)
        => op switch
        {
            BinaryOperator.Add => checked(left + right),
            BinaryOperator.Subtract => checked(left - right),
            BinaryOperator.Multiply => checked(left * right),
            BinaryOperator.Divide => right == 0m
                ? throw new ArgumentException("Division by zero is not allowed in filter expressions.")
                : left / right,
            BinaryOperator.Div => right == 0m
                ? throw new ArgumentException("Division by zero is not allowed in filter expressions.")
                : Math.Truncate(left / right),
            BinaryOperator.Modulo => right == 0m
                ? throw new ArgumentException("Division by zero is not allowed in filter expressions.")
                : left % right,
            BinaryOperator.Power when left == 0m && right > 0m => 0m,
            _ => null
        };

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
