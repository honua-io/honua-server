// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2;

internal static class Cql2BooleanExpression
{
    internal static bool IsPredicate(FilterExpression expression)
        => expression is BinaryExpression or UnaryExpression or SpatialPredicate or SpatialDistancePredicate
            or TemporalPredicate or ArrayPredicate or FunctionCall
            or Literal { Type: LiteralType.Boolean };
}
