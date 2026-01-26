// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
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

        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var orderClauses = new List<string>();
        foreach (var orderBy in query.OrderBy.Value)
        {
            var fieldSql = MapOrderByField(orderBy, ref paramIndex, parameters);
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            orderClauses.Add($"{fieldSql} {direction}");
        }

        if (orderClauses.Count > 0)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", orderClauses));
        }
    }

    private static string MapOrderByField(OrderByClause orderBy, ref int paramIndex, List<object> parameters)
    {
        var fieldName = orderBy.Field;

        if (!IsValidFieldName(fieldName))
        {
            throw new ArgumentException($"Invalid field name for ordering: {fieldName}");
        }

        var fieldLower = fieldName.ToLowerInvariant();

        if (fieldLower is DatabaseSchema.ObjectIdColumn or DatabaseSchema.ObjectIdColumnAlt or DatabaseSchema.IdColumn)
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
            FieldType.Integer => $"NULLIF({attributeValue}, '')::integer",
            FieldType.BigInteger => $"NULLIF({attributeValue}, '')::bigint",
            FieldType.Float => $"NULLIF({attributeValue}, '')::real",
            FieldType.Double => $"NULLIF({attributeValue}, '')::double precision",
            FieldType.Boolean => $"NULLIF({attributeValue}, '')::boolean",
            FieldType.DateTime => $"NULLIF({attributeValue}, '')::timestamptz",
            FieldType.Date => $"NULLIF({attributeValue}, '')::date",
            FieldType.Time => $"NULLIF({attributeValue}, '')::time",
            FieldType.Uuid => $"NULLIF({attributeValue}, '')::uuid",
            FieldType.String => attributeValue,
            _ => attributeValue
        };
    }
}
