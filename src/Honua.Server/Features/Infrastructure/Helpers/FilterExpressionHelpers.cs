// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Shared helper methods for filter expression validation and normalization.
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

    /// <summary>
    /// Recursively walks a filter expression tree and resolves property references
    /// against a layer definition, normalizing namespace prefixes and field names.
    /// </summary>
    internal static FilterExpression NormalizeFilterPropertyReferences(FilterExpression expression, LayerDefinition layer)
    {
        return expression switch
        {
            PropertyReference property => new PropertyReference(
                ResolveFieldName(layer, property.PropertyName, allowGeometryAlias: true) ??
                NormalizeIdentifier(property.PropertyName)),
            BinaryExpression binary => new BinaryExpression(
                NormalizeFilterPropertyReferences(binary.Left, layer),
                binary.Operator,
                NormalizeFilterPropertyReferences(binary.Right, layer)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                NormalizeFilterPropertyReferences(unary.Operand, layer)),
            SpatialPredicate spatial => new SpatialPredicate(
                spatial.Operator,
                NormalizeFilterPropertyReferences(spatial.Left, layer),
                NormalizeFilterPropertyReferences(spatial.Right, layer)),
            SpatialDistancePredicate distance => new SpatialDistancePredicate(
                distance.Operator,
                NormalizeFilterPropertyReferences(distance.Left, layer),
                NormalizeFilterPropertyReferences(distance.Right, layer),
                NormalizeFilterPropertyReferences(distance.Distance, layer)),
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                NormalizeFilterPropertyReferences(temporal.Left, layer),
                NormalizeFilterPropertyReferences(temporal.Right, layer)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                NormalizeFilterPropertyReferences(array.Left, layer),
                NormalizeFilterPropertyReferences(array.Right, layer)),
            FunctionCall function => new FunctionCall(
                function.FunctionName,
                function.Arguments.Select(argument => NormalizeFilterPropertyReferences(argument, layer)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(
                arrayLiteral.Elements.Select(element => NormalizeFilterPropertyReferences(element, layer)).ToArray()),
            ValueList valueList => new ValueList(
                valueList.Values.Select(value => NormalizeFilterPropertyReferences(value, layer)).ToArray()),
            _ => expression
        };
    }

    /// <summary>
    /// Strips namespace prefixes (slash and colon delimited) from an identifier.
    /// </summary>
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

    /// <summary>
    /// Resolves a requested field name against a layer definition, matching
    /// primary key, geometry, and attribute fields case-insensitively.
    /// </summary>
    internal static string? ResolveFieldName(LayerDefinition layer, string requestedName, bool allowGeometryAlias)
    {
        var normalized = NormalizeIdentifier(requestedName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (layer.PrimaryKeyField is not null &&
            (normalized.Equals("id", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("fid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(layer.PrimaryKeyField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return layer.PrimaryKeyField.Name;
        }

        if (allowGeometryAlias &&
            layer.GeometryField is not null &&
            (normalized.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return layer.GeometryField.Name;
        }

        return layer.Fields
            .FirstOrDefault(field => field.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }
}
