// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Infrastructure.Helpers;

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
            UnaryExpression unary => unary.Operator != UnaryOperator.Not || IsBooleanFilterExpression(unary.Operand),
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
    /// against a Metadata v2 resource, normalizing namespace prefixes and field names.
    /// </summary>
    internal static FilterExpression NormalizeFilterPropertyReferences(FilterExpression expression, MetadataV2Resource resource)
    {
        return expression switch
        {
            PropertyReference property => new PropertyReference(
                ResolveFieldName(resource, property.PropertyName, allowGeometryAlias: true) ??
                NormalizeIdentifier(property.PropertyName)),
            BinaryExpression binary => new BinaryExpression(
                NormalizeFilterPropertyReferences(binary.Left, resource),
                binary.Operator,
                NormalizeFilterPropertyReferences(binary.Right, resource)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                NormalizeFilterPropertyReferences(unary.Operand, resource)),
            SpatialPredicate spatial => new SpatialPredicate(
                spatial.Operator,
                NormalizeFilterPropertyReferences(spatial.Left, resource),
                NormalizeFilterPropertyReferences(spatial.Right, resource)),
            SpatialDistancePredicate distance => new SpatialDistancePredicate(
                distance.Operator,
                NormalizeFilterPropertyReferences(distance.Left, resource),
                NormalizeFilterPropertyReferences(distance.Right, resource),
                NormalizeFilterPropertyReferences(distance.Distance, resource)),
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                NormalizeFilterPropertyReferences(temporal.Left, resource),
                NormalizeFilterPropertyReferences(temporal.Right, resource)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                NormalizeFilterPropertyReferences(array.Left, resource),
                NormalizeFilterPropertyReferences(array.Right, resource)),
            FunctionCall function => new FunctionCall(
                function.FunctionName,
                function.Arguments.Select(argument => NormalizeFilterPropertyReferences(argument, resource)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(
                arrayLiteral.Elements.Select(element => NormalizeFilterPropertyReferences(element, resource)).ToArray()),
            ValueList valueList => new ValueList(
                valueList.Values.Select(value => NormalizeFilterPropertyReferences(value, resource)).ToArray()),
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
    /// Resolves a requested field name against a Metadata v2 resource, matching
    /// primary key, geometry, and schema fields case-insensitively.
    /// </summary>
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
