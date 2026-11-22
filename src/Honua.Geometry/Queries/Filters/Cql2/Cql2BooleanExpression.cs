// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2;

internal static class Cql2BooleanExpression
{
    private static readonly HashSet<string> BooleanFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ST_ISVALID",
        "ST_ISSIMPLE",
        "ST_ISCLOSED",
        "ST_ISEMPTY",
        "ST_RELATE",
        "ST_CONTAINS",
        "ST_WITHIN",
        "ST_INTERSECTS",
        "ST_DISJOINT",
        "ST_TOUCHES",
        "ST_CROSSES",
        "ST_OVERLAPS",
        "ST_EQUALS",
        "CONTAINS",
        "STARTSWITH",
        "ENDSWITH",
        "LIKE_ESCAPE",
        "NOT_LIKE_ESCAPE"
    };

    internal static bool IsPredicate(FilterExpression expression)
        => expression switch
        {
            BinaryExpression binary => IsPredicateOperator(binary.Operator),
            UnaryExpression unary => unary.Operator is UnaryOperator.Not or UnaryOperator.IsNull or UnaryOperator.IsNotNull,
            SpatialPredicate or SpatialDistancePredicate or TemporalPredicate or ArrayPredicate => true,
            Literal { Type: LiteralType.Boolean } => true,
            FunctionCall function => BooleanFunctionNames.Contains(function.FunctionName),
            _ => false
        };

    internal static void EnsurePredicateOperands(string operatorName, IReadOnlyList<FilterExpression> operands)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            if (!IsPredicate(operands[i]))
            {
                throw new ArgumentException($"Operator '{operatorName}' requires boolean or predicate operands.");
            }
        }
    }

    internal static void EnsureLikeOperands(string operatorName, FilterExpression target, FilterExpression pattern)
    {
        if (!IsValidLikeTarget(target))
        {
            throw new ArgumentException($"Operator '{operatorName}' requires a character expression as its first argument.");
        }

        if (!IsPatternExpression(pattern))
        {
            throw new ArgumentException($"Operator '{operatorName}' requires a string pattern or CASEI/ACCENTI wrapper as its second argument.");
        }
    }

    internal static bool IsValidLikeTarget(FilterExpression expression)
        => expression switch
        {
            Literal literal => literal.Type is LiteralType.Text or LiteralType.Null,
            GeometryLiteral or ArrayLiteral or IntervalLiteral or ValueList => false,
            BinaryExpression => false,
            UnaryExpression => false,
            SpatialPredicate or SpatialDistancePredicate or TemporalPredicate or ArrayPredicate => false,
            _ => true
        };

    internal static bool IsPatternExpression(FilterExpression expression)
        => expression switch
        {
            Literal { Type: LiteralType.Text } => true,
            FunctionCall { Arguments.Count: 1 } func
                when func.FunctionName.Equals("CASEI", StringComparison.OrdinalIgnoreCase)
                     || func.FunctionName.Equals("ACCENTI", StringComparison.OrdinalIgnoreCase)
                => IsPatternExpression(func.Arguments[0]),
            _ => false
        };

    private static bool IsPredicateOperator(BinaryOperator op)
        => op is BinaryOperator.And or BinaryOperator.Or
            or BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.Like or BinaryOperator.NotLike
            or BinaryOperator.In or BinaryOperator.NotIn;
}
