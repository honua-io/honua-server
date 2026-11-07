// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Infrastructure.Helpers;

internal static class FilterExpressionHelpers
{
    internal static bool IsBooleanFilterExpression(FilterExpression expression)
    {
        return expression switch
        {
            BinaryExpression => true,
            UnaryExpression unary => unary.Operator != UnaryOperator.Not || IsBooleanFilterExpression(unary.Operand),
            SpatialPredicate => true,
            SpatialDistancePredicate => true,
            TemporalPredicate => true,
            ArrayPredicate => true,
            Literal literal => literal.Type == LiteralType.Boolean,
            _ => false
        };
    }

    internal static FilterExpression NormalizeFilterPropertyReferences(
        FilterExpression expression,
        MetadataV2Resource resource,
        Func<string, string?>? protocolFieldResolver = null)
    {
        return RewriteProperties(expression, property => new PropertyReference(
            protocolFieldResolver?.Invoke(property.PropertyName) ??
            ResolveFieldName(resource, property.PropertyName, allowGeometryAlias: true) ??
            NormalizeIdentifier(property.PropertyName)));
    }

    internal static FilterExpression RewriteProperties(
        FilterExpression expression, Func<PropertyReference, FilterExpression> rewrite)
    {
        return expression switch
        {
            PropertyReference property => rewrite(property),
            BinaryExpression binary => new BinaryExpression(
                RewriteProperties(binary.Left, rewrite),
                binary.Operator,
                RewriteProperties(binary.Right, rewrite)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteProperties(unary.Operand, rewrite)),
            // 'with' preserves protocol-scoped flags (SpatialPredicate.Geodesic,
            // SpatialDistancePredicate.DistanceInMeters) that a positional reconstruction would
            // silently reset to their defaults.
            SpatialPredicate spatial => spatial with
            {
                Left = RewriteProperties(spatial.Left, rewrite),
                Right = RewriteProperties(spatial.Right, rewrite)
            },
            SpatialDistancePredicate distance => distance with
            {
                Left = RewriteProperties(distance.Left, rewrite),
                Right = RewriteProperties(distance.Right, rewrite),
                Distance = RewriteProperties(distance.Distance, rewrite)
            },
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                RewriteProperties(temporal.Left, rewrite),
                RewriteProperties(temporal.Right, rewrite)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                RewriteProperties(array.Left, rewrite),
                RewriteProperties(array.Right, rewrite)),
            FunctionCall function => new FunctionCall(
                function.FunctionName,
                function.Arguments.Select(argument => RewriteProperties(argument, rewrite)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(
                arrayLiteral.Elements.Select(element => RewriteProperties(element, rewrite)).ToArray()),
            ValueList valueList => new ValueList(
                valueList.Values.Select(value => RewriteProperties(value, rewrite)).ToArray()),
            _ => expression
        };
    }

    internal static string NormalizeIdentifier(string identifier)
    {
        var normalized = identifier.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        var colonIndex = normalized.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < normalized.Length - 1)
        {
            normalized = normalized[(colonIndex + 1)..];
        }

        return normalized;
    }

    internal static string? ResolveFieldName(MetadataV2Resource resource, string requestedName, bool allowGeometryAlias)
    {
        var normalized = NormalizeIdentifier(requestedName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var primaryKey = resource.FindPrimaryIdField();
        if (primaryKey is not null &&
            (normalized.Equals("id", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("fid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(primaryKey.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return primaryKey.Name;
        }

        var geometryField = resource.FindPrimaryGeometryField();
        if (allowGeometryAlias &&
            geometryField is not null &&
            (normalized.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(geometryField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return geometryField.Name;
        }

        return resource.SchemaFields
            .FirstOrDefault(field => field.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }
}
