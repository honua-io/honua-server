// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Shared parse-time validation helpers for boolean predicate context and zero divisors
/// across filter languages (OData, GeoServices SQL).
/// </summary>
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
            string s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == 0m,
            _ => false
        };
    }

    private static bool IsBooleanCore(FilterExpression expression, Func<FunctionCall, bool> isBooleanFunction) =>
        expression switch
        {
            PropertyReference => true,
            Literal { Type: LiteralType.Boolean } => true,
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
