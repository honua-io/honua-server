// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private static void AppendOrderByClause(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        if (query.SpatialFilter.HasValue &&
            query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            return;
        }

        var orderClauses = new List<string>();
        if (query.OrderBy.HasValue && !query.OrderBy.Value.IsDefaultOrEmpty)
        {
            foreach (var orderBy in query.OrderBy.Value)
            {
                var fieldSql = MapOrderByField(orderBy, ref paramIndex, parameters);
                var direction = orderBy.Ascending ? "ASC" : "DESC";
                orderClauses.Add($"{fieldSql} {direction}");
            }
        }

        // Any server-side page needs deterministic order when the caller did not
        // provide one, otherwise first pages can drift between executions.
        if (RequiresStablePageOrder(query) && !HasObjectIdOrdering(query))
        {
            orderClauses.Add($"{DatabaseSchema.ObjectIdColumn} ASC");
        }

        if (orderClauses.Count > 0)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", orderClauses));
        }
    }

    private static bool RequiresStablePageOrder(FeatureQuery query)
        => query.Offset.HasValue ||
           query.SpatialFilter.HasValue ||
           (query.Limit.HasValue && !IsUnorderedFirstPageLikeFilter(query));

    private static bool IsUnorderedFirstPageLikeFilter(FeatureQuery query)
    {
        if (!query.Limit.HasValue ||
            query.Offset.HasValue ||
            query.SpatialFilter.HasValue ||
            query.OrderBy.HasValue)
        {
            return false;
        }

        return query.SqlFilter?.Sql.Contains(" LIKE ", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasObjectIdOrdering(FeatureQuery query)
    {
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (var orderBy in query.OrderBy.Value)
        {
            var fieldLower = orderBy.Field.ToLowerInvariant();
            if (fieldLower is DatabaseSchema.ObjectIdColumn or DatabaseSchema.ObjectIdColumnAlt ||
                fieldLower is DatabaseSchema.IdColumn && !orderBy.FieldType.HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private static string MapOrderByField(OrderByClause orderBy, ref int paramIndex, List<object> parameters)
    {
        var fieldName = orderBy.Field;

        if (!IsValidFieldName(fieldName))
        {
            throw new ArgumentException($"Invalid field name for ordering: {fieldName}");
        }

        var fieldLower = fieldName.ToLowerInvariant();

        if (fieldLower is DatabaseSchema.ObjectIdColumn or DatabaseSchema.ObjectIdColumnAlt ||
            fieldLower is DatabaseSchema.IdColumn && !orderBy.FieldType.HasValue)
        {
            return DatabaseSchema.ObjectIdColumn;
        }

        if (fieldLower is "created_at" or "updated_at")
        {
            return fieldLower;
        }

        if (fieldLower is DatabaseSchema.LayerIdColumnAlt or DatabaseSchema.LayerIdColumn)
        {
            return DatabaseSchema.LayerIdColumn;
        }

        var attributeValue = BuildAttributeValueExpression(fieldName, ref paramIndex, parameters);

        if (!orderBy.FieldType.HasValue)
        {
            return attributeValue;
        }

        return orderBy.FieldType.Value switch
        {
            MetadataV2FieldType.Integer => $"NULLIF({attributeValue}, '')::integer",
            MetadataV2FieldType.BigInteger => $"NULLIF({attributeValue}, '')::bigint",
            MetadataV2FieldType.Float => $"NULLIF({attributeValue}, '')::real",
            MetadataV2FieldType.Double => $"NULLIF({attributeValue}, '')::double precision",
            MetadataV2FieldType.Boolean => $"NULLIF({attributeValue}, '')::boolean",
            MetadataV2FieldType.DateTime => BuildEpochAwareOrderByCast(attributeValue, "timestamptz"),
            MetadataV2FieldType.Date => BuildEpochAwareOrderByCast(attributeValue, "date"),
            MetadataV2FieldType.Time => $"NULLIF({attributeValue}, '')::time",
            MetadataV2FieldType.Uuid => $"NULLIF({attributeValue}, '')::uuid",
            MetadataV2FieldType.String => attributeValue,
            _ => attributeValue
        };
    }

    // A date/datetime attribute stored in JSONB may be ISO-8601 text (seeded rows) or
    // epoch-milliseconds rendered as text (Esri applyEdits). A bare ::timestamptz/::date
    // cast on epoch-ms text raises SQLSTATE 22008 (HTTP 500) — the same hazard the
    // CQL2 filter translator already guards via BuildEpochAwareTemporalCast. Detect the
    // numeric form and convert via to_timestamp; otherwise cast the ISO text.
    private static string BuildEpochAwareOrderByCast(string attributeValue, string castType)
    {
        var nullSafe = $"NULLIF({attributeValue}, '')";
        var timestamp = $"CASE WHEN {nullSafe} ~ '^-?[0-9]+$' " +
                        $"THEN to_timestamp({nullSafe}::double precision / 1000.0) " +
                        $"ELSE {nullSafe}::timestamptz END";
        return castType == "timestamptz" ? timestamp : $"({timestamp})::{castType}";
    }
}
