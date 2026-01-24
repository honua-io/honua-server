// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

internal static class FilterExpressionHelpers
{
    internal static BinaryExpression BuildBetweenExpression(
        FilterExpression target,
        FilterExpression lower,
        FilterExpression upper,
        bool negate)
    {
        var lowerExpr = new BinaryExpression(target, BinaryOperator.GreaterThanOrEqual, lower);
        var upperExpr = new BinaryExpression(target, BinaryOperator.LessThanOrEqual, upper);
        var combined = new BinaryExpression(lowerExpr, BinaryOperator.And, upperExpr);

        if (!negate)
        {
            return combined;
        }

        var lowerNot = new BinaryExpression(target, BinaryOperator.LessThan, lower);
        var upperNot = new BinaryExpression(target, BinaryOperator.GreaterThan, upper);
        return new BinaryExpression(lowerNot, BinaryOperator.Or, upperNot);
    }
}
