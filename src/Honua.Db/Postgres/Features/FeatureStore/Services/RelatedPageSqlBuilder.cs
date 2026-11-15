// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class RelatedPageSqlBuilder
{
    public static (string Sql, List<object> Parameters) Build(RelatedPageReadRequest request, string table, string geometry)
    {
        var relationship = request.Query.Relationship;
        var parameters = new List<object> { request.LayerId, relationship.ObjectIds, relationship.RelatedLayerId!.Value };
        var originKey = KeyExpression(relationship.OriginForeignKeyField!, parameters);
        var destinationKey = KeyExpression(relationship.DestinationForeignKeyField!, parameters);
        var sql = new StringBuilder($"WITH origins AS (SELECT objectid, {originKey} AS join_key FROM {table} WHERE layer_id = $1 AND objectid = ANY($2)");
        AppendFilter(sql, parameters, request.Security.OriginFilter);
        sql.Append(CultureInfo.InvariantCulture,
            $"), destinations AS (SELECT objectid, {geometry}, attributes, {destinationKey} AS join_key FROM {table} WHERE layer_id = $3");
        AppendFilter(sql, parameters, request.Security.RelatedFilter);
        AppendCallerFilter(sql, parameters, relationship);
        sql.Append(") SELECT d.objectid, d.geometry, d.attributes, o.objectid AS origin_id FROM origins o LEFT JOIN destinations d ON o.join_key = d.join_key");
        AppendCursor(sql, parameters, request.Query.After);
        parameters.Add(request.Query.PositionLimit + 1);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY o.objectid, d.objectid NULLS FIRST LIMIT ${parameters.Count}");
        return (sql.ToString(), parameters);
    }

    private static string KeyExpression(string field, List<object> parameters)
    {
        if (field.Equals("objectid", StringComparison.OrdinalIgnoreCase) || field.Equals("object_id", StringComparison.OrdinalIgnoreCase))
        {
            return "objectid::text";
        }
        parameters.Add(field);
        return string.Create(CultureInfo.InvariantCulture, $"attributes->>${parameters.Count}");
    }

    private static void AppendFilter(StringBuilder sql, List<object> parameters, SqlFragment? filter)
    {
        if (filter is null)
        {
            return;
        }
        var index = parameters.Count + 1;
        var converted = FeatureQueryBuilder.ConvertNamedParametersToPositional(filter.Sql, ref index);
        sql.Append(CultureInfo.InvariantCulture, $" AND ({converted})");
        parameters.AddRange(filter.Parameters.Select(value => value ?? DBNull.Value));
    }

    private static void AppendCallerFilter(StringBuilder sql, List<object> parameters, RelatedQuery relationship)
    {
        if (relationship.SqlFilter is { } filter)
        {
            AppendFilter(sql, parameters, filter);
        }
        else if (!string.IsNullOrWhiteSpace(relationship.Where))
        {
            var index = parameters.Count + 1;
            var clause = FeatureQueryBuilder.ParseAndParameterizeWhereClause(relationship.Where.Trim(), ref index, parameters);
            sql.Append(CultureInfo.InvariantCulture, $" AND ({clause})");
        }
    }

    private static void AppendCursor(StringBuilder sql, List<object> parameters, RelatedPageCursor? after)
    {
        if (after is not { } cursor)
        {
            return;
        }
        parameters.Add(cursor.OriginId);
        var originParameter = parameters.Count;
        sql.Append(CultureInfo.InvariantCulture, $" WHERE (o.objectid > ${originParameter}");
        if (cursor.RelatedId is { } relatedId)
        {
            parameters.Add(relatedId);
            sql.Append(CultureInfo.InvariantCulture, $" OR (o.objectid = ${originParameter} AND d.objectid > ${parameters.Count})");
        }
        sql.Append(')');
    }
}
