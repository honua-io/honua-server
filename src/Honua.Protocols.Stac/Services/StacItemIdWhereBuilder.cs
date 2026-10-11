// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Stac;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Builds provider-neutral GeoServices SQL predicates for routed STAC item identifiers.
/// </summary>
internal static class StacItemIdWhereBuilder
{
    public static ImmutableArray<MetadataV2Field> GetCandidateFields(MetadataV2Resource resource)
    {
        var source = StacItemIdentity.Resolve(resource);
        if (source.Field is null)
        {
            return ImmutableArray<MetadataV2Field>.Empty;
        }

        if (!source.IsDeclaredIdentity)
        {
            return ImmutableArray.Create(source.Field);
        }

        // A blank declared identity is published as the object id. Include the primary
        // key so a client can fetch that published id, while rows with a real identity
        // still fail the published-id check in the query executor.
        var primary = resource.FindPrimaryIdField();
        if (primary is null ||
            string.Equals(primary.Name, source.Field.Name, StringComparison.OrdinalIgnoreCase))
        {
            return ImmutableArray.Create(source.Field);
        }

        return ImmutableArray.Create(source.Field, primary);
    }

    /// <summary>
    /// Attribute names an unbound item lookup may match. A configured identity field
    /// replaces the canonical <c>stac_id</c>/<c>item_id</c>/<c>id</c> list.
    /// </summary>
    public static IReadOnlyList<string> UnboundAttributeNames(MetadataV2Resource resource)
    {
        var source = StacItemIdentity.Resolve(resource);
        if (source.IsDeclaredIdentity &&
            source.Field is { } field &&
            !string.IsNullOrWhiteSpace(field.Name))
        {
            return [field.Name];
        }

        return StacItemIdentity.CanonicalNames;
    }

    public static string BuildUnboundAttributePredicate(MetadataV2Resource resource)
        => string.Join(
            " OR ",
            UnboundAttributeNames(resource).Select(static name =>
                $"attributes->>'{name.Replace("'", "''", StringComparison.Ordinal)}' = @p0"));

    public static bool TryBuildFieldMatch(
        MetadataV2Field field,
        ImmutableArray<string> itemIds,
        out string where)
    {
        var literals = itemIds
            .Select(itemId => TryFormatLiteral(field.Type, itemId, out var literal) ? literal : null)
            .Where(static literal => literal is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (literals.Length == 0)
        {
            where = string.Empty;
            return false;
        }

        where = literals.Length == 1
            ? $"{field.Name} = {literals[0]}"
            : $"{field.Name} IN ({string.Join(", ", literals)})";
        return true;
    }

    public static string Combine(string? existing, string added)
        => string.IsNullOrWhiteSpace(existing) ? added : $"({existing}) AND ({added})";

    private static bool TryFormatLiteral(MetadataV2FieldType fieldType, string itemId, out string literal)
    {
        switch (fieldType)
        {
            case MetadataV2FieldType.Integer
                when int.TryParse(itemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                literal = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case MetadataV2FieldType.BigInteger
                when long.TryParse(itemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bigInteger):
                literal = bigInteger.ToString(CultureInfo.InvariantCulture);
                return true;
            case MetadataV2FieldType.Double or MetadataV2FieldType.Float
                when double.TryParse(itemId, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatingPoint):
                literal = floatingPoint.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case MetadataV2FieldType.String or MetadataV2FieldType.Uuid or MetadataV2FieldType.Unknown:
                literal = $"'{itemId.Replace("'", "''", StringComparison.Ordinal)}'";
                return true;
            default:
                literal = string.Empty;
                return false;
        }
    }
}
