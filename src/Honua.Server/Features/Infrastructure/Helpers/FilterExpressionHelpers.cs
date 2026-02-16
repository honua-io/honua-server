// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Shared helper methods for filter expression validation.
/// </summary>
internal static class FilterExpressionHelpers
{
    /// <summary>
    /// Determines whether a filter expression resolves to a boolean value,
    /// suitable for use as a WHERE clause predicate.
    /// </summary>
    internal static bool IsBooleanFilterExpression(FilterExpression expression)
    {
        return expression switch
        {
            BinaryExpression => true,
            UnaryExpression => true,
            SpatialPredicate => true,
            SpatialDistancePredicate => true,
            TemporalPredicate => true,
            ArrayPredicate => true,
            Literal literal => literal.Type == LiteralType.Boolean,
            _ => false
        };
    }
}
