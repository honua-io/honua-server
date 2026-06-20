// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    public CoreParameterizedQuery BuildTopFeaturesQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(query, "top-features");
        if (!query.TopFilter.HasValue)
        {
            throw new ArgumentException("TopFilter must be specified for a top features query.", nameof(query));
        }

        var topFilter = query.TopFilter.Value;
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            // Build outer SELECT * FROM (inner subquery with ROW_NUMBER) WHERE rn <= topCount
            sql.Append("SELECT sub.* FROM (SELECT ");

            // Select columns: objectid, geometry, attributes (must match ReadFeatureAsync ordinal positions)
            sql.Append(DatabaseSchema.ObjectIdColumn);

            // Honor outSR (query.OutputSrid) by routing geometry through the shared
            // geometry-select expression, which applies ST_Transform when an output SRID
            // is requested (#1906) AND preserves Z/M through ST_AsEWKB when the query asks
            // for it (#1907) — mirroring the normal query path. The shared expression
            // already chooses ST_AsEWKB vs ST_AsBinary based on query.IncludeZ/IncludeM.
            sql.Append(", ");
            sql.Append(_geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query));

            sql.Append(CultureInfo.InvariantCulture, $", {DatabaseSchema.AttributesColumn}");

            // Add ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)
            sql.Append(", ROW_NUMBER() OVER (PARTITION BY ");
            AppendFieldList(sql, topFilter.GroupByFields);
            sql.Append(" ORDER BY ");
            AppendOrderByFieldList(sql, topFilter.OrderByFields);
            sql.Append(") AS rn");

            // FROM table WHERE layer_id = $1
            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            // Append additional filters
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            // Close subquery and filter by row number
            var topCountParameter = paramIndex++;
            parameters.Add(topFilter.TopCount);
            sql.Append(CultureInfo.InvariantCulture,
                $") sub WHERE sub.rn <= ${topCountParameter}");

            // resultRecordCount / resultOffset paging. The LIMIT/OFFSET values are bound by
            // AddQueryParameters (AddRegularPaginationParameters / AddOffsetParameterIfEmitted)
            // after the where/topCount parameters, so the placeholders must be the trailing
            // ones and emitted only when the corresponding value is present, keeping the bound
            // parameter count aligned with the placeholders (#1906).
            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static void AppendFieldList(StringBuilder sql, ImmutableArray<string> fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            if (!IsValidFieldName(fields[i]))
            {
                throw new ArgumentException($"Invalid field name: {fields[i]}");
            }

            sql.Append(GetFieldExpression(fields[i]));
        }
    }

    private static void AppendOrderByFieldList(StringBuilder sql, ImmutableArray<OrderByClause> orderByFields)
    {
        for (var i = 0; i < orderByFields.Length; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            if (!IsValidFieldName(orderByFields[i].Field))
            {
                throw new ArgumentException($"Invalid order-by field name: {orderByFields[i].Field}");
            }

            sql.Append(GetFieldExpression(orderByFields[i].Field));
            sql.Append(orderByFields[i].Ascending ? " ASC" : " DESC");
        }
    }
}
