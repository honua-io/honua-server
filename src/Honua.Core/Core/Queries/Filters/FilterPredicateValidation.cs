// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

public static class FilterPredicateValidation
{
    private static readonly HashSet<string> _geoServicesSqlBooleanFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONTAINS", "STARTSWITH", "ENDSWITH",
        "LIKE_ESCAPE", "NOT_LIKE_ESCAPE",
        "ST_CONTAINS", "ST_WITHIN", "ST_INTERSECTS", "ST_DISJOINT",
        "ST_TOUCHES", "ST_CROSSES", "ST_OVERLAPS", "ST_EQUALS",
        "CASE", "COALESCE", "NULLIF", "IIF"
    };

    public static bool IsODataBooleanOperand(FilterExpression expression) =>
        IsBooleanCore(expression, static fn =>
            string.Equals(fn.FunctionName, "ST_RELATE", StringComparison.OrdinalIgnoreCase));

    public static bool IsGeoServicesSqlBooleanExpression(FilterExpression expression) =>
        IsBooleanCore(expression, IsGeoServicesSqlBooleanFunction);

    public static bool IsZeroNumericLiteral(FilterExpression expression)
    {
        var target = expression;
        while (target is UnaryExpression { Operator: UnaryOperator.Negate } neg)
        {
            target = neg.Operand;
        }

        if (target is not Literal { Type: LiteralType.Number, Value: var value })
        {
            return false;
        }

        return value switch
        {
            int i => i == 0,
            long l => l == 0,
            decimal m => m == 0m,
            double d => d == 0d,
            float f => f == 0f,
            string s => IsZeroNumericText(s),
            _ => false
        };
    }

    private static bool IsZeroNumericText(string text)
    {
        var span = StripSign(text.AsSpan().Trim());
        if (span.IsEmpty)
        {
            return false;
        }

        var expIndex = span.IndexOfAny('e', 'E');
        var significand = expIndex >= 0 ? span[..expIndex] : span;
        if (!IsZeroSignificand(significand))
        {
            return false;
        }

        return expIndex < 0 || IsValidExponent(span[(expIndex + 1)..]);
    }

    private static ReadOnlySpan<char> StripSign(ReadOnlySpan<char> span) =>
        span.Length > 0 && span[0] is '+' or '-' ? span[1..] : span;

    private static bool IsZeroSignificand(ReadOnlySpan<char> span)
    {
        var hasDigit = false;
        var seenDot = false;
        foreach (var c in span)
        {
            if (c == '.')
            {
                if (seenDot)
                {
                    return false;
                }

                seenDot = true;
                continue;
            }

            if (c != '0')
            {
                return false;
            }

            hasDigit = true;
        }

        return hasDigit;
    }

    private static bool IsValidExponent(ReadOnlySpan<char> span)
    {
        var digits = StripSign(span);
        if (digits.IsEmpty)
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBooleanCore(FilterExpression expression, Func<FunctionCall, bool> isBooleanFunction) =>
        expression switch
        {
            PropertyReference => true,
            Literal { Type: LiteralType.Boolean or LiteralType.Null } => true,
            SpatialPredicate => true,
            UnaryExpression { Operator: UnaryOperator.Not or UnaryOperator.IsNull or UnaryOperator.IsNotNull } => true,
            BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo } => false,
            BinaryExpression => true,
            FunctionCall fn => isBooleanFunction(fn),
            _ => false
        };

    private static bool IsGeoServicesSqlBooleanFunction(FunctionCall fn)
    {
        if (_geoServicesSqlBooleanFunctions.Contains(fn.FunctionName))
        {
            return true;
        }

        if (string.Equals(fn.FunctionName, "CAST", StringComparison.OrdinalIgnoreCase)
            && fn.Arguments is [_, Literal { Value: string typeName }])
        {
            return typeName.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
                || typeName.Equals("BOOL", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
