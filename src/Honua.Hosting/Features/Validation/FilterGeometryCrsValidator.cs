// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Infrastructure.Validation;

internal static class FilterGeometryCrsValidator
{
    public static bool HasExplicitCrs(GeometryLiteral geometry)
    {
        if (string.IsNullOrWhiteSpace(geometry.OriginalFormat))
        {
            return false;
        }

        if (geometry.OriginalFormat.Contains("SRID=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = geometry.OriginalFormat.TrimStart();
        return trimmed.StartsWith('{') &&
               geometry.OriginalFormat.Contains("\"crs\"", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryFindUnsupportedExplicitGeometryCrs(
        FilterExpression filterExpression,
        IReadOnlySet<int> supportedSrids,
        out int unsupportedSrid)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        ArgumentNullException.ThrowIfNull(supportedSrids);

        return TryFindUnsupportedExplicitGeometryCrs(filterExpression, supportedSrids.Contains, out unsupportedSrid);
    }

    public static bool TryFindUnsupportedExplicitGeometryCrs(
        FilterExpression filterExpression,
        Func<int, bool> isSupportedSrid,
        out int unsupportedSrid)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        ArgumentNullException.ThrowIfNull(isSupportedSrid);

        return filterExpression switch
        {
            GeometryLiteral geometry => TryValidateGeometryLiteral(geometry, isSupportedSrid, out unsupportedSrid),
            BinaryExpression binary =>
                TryFindUnsupportedExplicitGeometryCrs(binary.Left, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(binary.Right, isSupportedSrid, out unsupportedSrid),
            UnaryExpression unary => TryFindUnsupportedExplicitGeometryCrs(unary.Operand, isSupportedSrid, out unsupportedSrid),
            SpatialPredicate spatial =>
                TryFindUnsupportedExplicitGeometryCrs(spatial.Left, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(spatial.Right, isSupportedSrid, out unsupportedSrid),
            SpatialDistancePredicate spatialDistance =>
                TryFindUnsupportedExplicitGeometryCrs(spatialDistance.Left, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(spatialDistance.Right, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(spatialDistance.Distance, isSupportedSrid, out unsupportedSrid),
            TemporalPredicate temporal =>
                TryFindUnsupportedExplicitGeometryCrs(temporal.Left, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(temporal.Right, isSupportedSrid, out unsupportedSrid),
            ArrayPredicate array =>
                TryFindUnsupportedExplicitGeometryCrs(array.Left, isSupportedSrid, out unsupportedSrid) ||
                TryFindUnsupportedExplicitGeometryCrs(array.Right, isSupportedSrid, out unsupportedSrid),
            FunctionCall functionCall => TryFindUnsupportedExplicitGeometryCrs(functionCall.Arguments, isSupportedSrid, out unsupportedSrid),
            ArrayLiteral arrayLiteral => TryFindUnsupportedExplicitGeometryCrs(arrayLiteral.Elements, isSupportedSrid, out unsupportedSrid),
            ValueList valueList => TryFindUnsupportedExplicitGeometryCrs(valueList.Values, isSupportedSrid, out unsupportedSrid),
            _ => ReturnFalse(out unsupportedSrid)
        };
    }

    public static IReadOnlySet<int> GetExplicitGeometrySrids(FilterExpression filterExpression)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);

        var srids = new HashSet<int>();
        CollectExplicitGeometrySrids(filterExpression, srids);
        return srids;
    }

    private static bool TryFindUnsupportedExplicitGeometryCrs(
        IReadOnlyList<FilterExpression> expressions,
        Func<int, bool> isSupportedSrid,
        out int unsupportedSrid)
    {
        foreach (var expression in expressions)
        {
            if (TryFindUnsupportedExplicitGeometryCrs(expression, isSupportedSrid, out unsupportedSrid))
            {
                return true;
            }
        }

        unsupportedSrid = 0;
        return false;
    }

    private static void CollectExplicitGeometrySrids(
        FilterExpression filterExpression,
        ISet<int> explicitSrids)
    {
        switch (filterExpression)
        {
            case GeometryLiteral geometry when HasExplicitCrs(geometry) && geometry.Srid > 0:
                explicitSrids.Add(geometry.Srid);
                break;
            case BinaryExpression binary:
                CollectExplicitGeometrySrids(binary.Left, explicitSrids);
                CollectExplicitGeometrySrids(binary.Right, explicitSrids);
                break;
            case UnaryExpression unary:
                CollectExplicitGeometrySrids(unary.Operand, explicitSrids);
                break;
            case SpatialPredicate spatial:
                CollectExplicitGeometrySrids(spatial.Left, explicitSrids);
                CollectExplicitGeometrySrids(spatial.Right, explicitSrids);
                break;
            case SpatialDistancePredicate spatialDistance:
                CollectExplicitGeometrySrids(spatialDistance.Left, explicitSrids);
                CollectExplicitGeometrySrids(spatialDistance.Right, explicitSrids);
                CollectExplicitGeometrySrids(spatialDistance.Distance, explicitSrids);
                break;
            case TemporalPredicate temporal:
                CollectExplicitGeometrySrids(temporal.Left, explicitSrids);
                CollectExplicitGeometrySrids(temporal.Right, explicitSrids);
                break;
            case ArrayPredicate array:
                CollectExplicitGeometrySrids(array.Left, explicitSrids);
                CollectExplicitGeometrySrids(array.Right, explicitSrids);
                break;
            case FunctionCall functionCall:
                CollectExplicitGeometrySrids(functionCall.Arguments, explicitSrids);
                break;
            case ArrayLiteral arrayLiteral:
                CollectExplicitGeometrySrids(arrayLiteral.Elements, explicitSrids);
                break;
            case ValueList valueList:
                CollectExplicitGeometrySrids(valueList.Values, explicitSrids);
                break;
        }
    }

    private static void CollectExplicitGeometrySrids(
        IReadOnlyList<FilterExpression> expressions,
        ISet<int> explicitSrids)
    {
        foreach (var expression in expressions)
        {
            CollectExplicitGeometrySrids(expression, explicitSrids);
        }
    }

    private static bool TryValidateGeometryLiteral(
        GeometryLiteral geometry,
        Func<int, bool> isSupportedSrid,
        out int unsupportedSrid)
    {
        if (!HasExplicitCrs(geometry) || isSupportedSrid(geometry.Srid))
        {
            unsupportedSrid = 0;
            return false;
        }

        unsupportedSrid = geometry.Srid;
        return true;
    }

    private static bool ReturnFalse(out int unsupportedSrid)
    {
        unsupportedSrid = 0;
        return false;
    }
}
