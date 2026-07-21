// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Builds provider-neutral GeoServices SQL predicates for routed STAC item identifiers.
/// </summary>
internal static class StacItemIdWhereBuilder
{
    private static readonly ImmutableArray<string> CanonicalKeys = ["stac_id", "item_id", "id"];

    public static ImmutableArray<MetadataV2Field> GetCandidateFields(MetadataV2Resource resource)
    {
        var fields = ImmutableArray.CreateBuilder<MetadataV2Field>();
        foreach (var canonicalKey in CanonicalKeys)
        {
            var field = resource.SchemaFields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, canonicalKey, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        var primaryIdField = resource.FindPrimaryIdField();
        if (primaryIdField is not null &&
            !CanonicalKeys.Any(key => string.Equals(key, primaryIdField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            fields.Add(primaryIdField);
        }

        return fields.ToImmutable();
    }

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
