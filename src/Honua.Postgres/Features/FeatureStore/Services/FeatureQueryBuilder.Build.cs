// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Builds SQL queries for PostgreSQL feature store operations
/// </summary>
internal sealed partial class FeatureQueryBuilder : IFeatureQueryBuilder
{
    private readonly ObjectPool<StringBuilder> _stringBuilderPool;
    private readonly IGeometryProcessor _geometryProcessor;
    private readonly string _tableName;

    public FeatureQueryBuilder(
        ObjectPool<StringBuilder> stringBuilderPool,
        IGeometryProcessor geometryProcessor,
        string? schemaName = null)
    {
        _stringBuilderPool = stringBuilderPool ?? throw new ArgumentNullException(nameof(stringBuilderPool));
        _geometryProcessor = geometryProcessor ?? throw new ArgumentNullException(nameof(geometryProcessor));

        _tableName = DatabaseSchema.GetFeaturesTableName(schemaName);
    }

    public CoreParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            BuildSelectClause(sql, query, geometryStorageType, isKnnQuery, spatialFilter, ref paramIndex);
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);
            AppendKnnOrdering(sql, isKnnQuery, spatialFilter, query, geometryStorageType, ref paramIndex);
            AppendPagination(sql, isKnnQuery, query, spatialFilter, ref paramIndex);

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            sql.Append(CultureInfo.InvariantCulture, $"SELECT {DatabaseSchema.ObjectIdColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);

            if (isKnnQuery)
            {
                AppendKnnOrdering(sql, true, spatialFilter, query, geometryStorageType, ref paramIndex);
            }
            else if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
            {
                sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {DatabaseSchema.ObjectIdColumn}");
            }

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
                parameters.Add(query.Limit.Value);
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildSelectFlatGeobufQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var geometryExpression = _geometryProcessor.GetGeometryOperand(
                geometryStorageType,
                null,
                query.SpatialReferenceSrid);

            sql.Append("SELECT ST_AsFlatGeobuf(q, true, 'geometry') FROM (");
            sql.Append(CultureInfo.InvariantCulture, $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometryExpression} AS geometry");

            if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
            {
                AppendFlatGeobufAttributeProjection(sql, query.OutFields.Value, ref paramIndex, parameters);
            }
            else
            {
                sql.Append(CultureInfo.InvariantCulture, $", {DatabaseSchema.AttributesColumn}::text AS attributes");
            }

            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);

            if (isKnnQuery)
            {
                AppendKnnOrdering(sql, true, spatialFilter, query, geometryStorageType, ref paramIndex);
            }

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
                parameters.Add(query.Limit.Value);
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            sql.Append(") AS q");
            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            BuildGmlSelectClause(sql, query, geometryStorageType, isKnnQuery, spatialFilter, ref paramIndex);
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);
            AppendKnnOrdering(sql, isKnnQuery, spatialFilter, query, geometryStorageType, ref paramIndex);
            AppendPagination(sql, isKnnQuery, query, spatialFilter, ref paramIndex);

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static void AppendFlatGeobufAttributeProjection(
        StringBuilder sql,
        ImmutableArray<string> outFields,
        ref int paramIndex,
        List<object> parameters)
    {
        var emittedFieldCount = 0;
        foreach (var requestedField in outFields)
        {
            if (string.IsNullOrWhiteSpace(requestedField))
            {
                continue;
            }

            if (requestedField.Equals("*", StringComparison.Ordinal))
            {
                sql.Append(CultureInfo.InvariantCulture, $", {DatabaseSchema.AttributesColumn}::text AS attributes");
                return;
            }

            var mappedField = DatabaseSchema.MapFieldName(requestedField.Trim());
            if (!IsValidFieldName(mappedField))
            {
                throw new ArgumentException($"Invalid outField name: {requestedField}");
            }

            if (mappedField.Equals(DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase) ||
                mappedField.Equals(DatabaseSchema.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributeValue = BuildAttributeValueExpression(mappedField, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture, $", {attributeValue} AS \"{mappedField}\"");
            emittedFieldCount++;
        }

        if (emittedFieldCount == 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {DatabaseSchema.AttributesColumn}::text AS attributes");
        }
    }

    public CoreParameterizedQuery BuildCountQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append(CultureInfo.InvariantCulture, $"SELECT COUNT(*) FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            var paramIndex = 2;
            var parameters = new List<object>();

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);

            sql.Append(CultureInfo.InvariantCulture, $@"
                SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {DatabaseSchema.AttributesColumn}, COUNT(*) OVER() as total_count
                FROM {_tableName}
                WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
                parameters.Add(query.Limit.Value);
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);

            sql.Append(CultureInfo.InvariantCulture, $@"
                SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn}, COUNT(*) OVER() as total_count
                FROM {_tableName}
                WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
                parameters.Add(query.Limit.Value);
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildExtentQuery(
        int layerId,
        FeatureQuery? query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var effectiveQuery = query ?? new FeatureQuery();
        var extentExpression = _geometryProcessor.GetGeometryOperand(geometryStorageType, null, effectiveQuery.SpatialReferenceSrid);

        if (effectiveQuery.OutputSrid.HasValue &&
            effectiveQuery.SpatialReferenceSrid.HasValue &&
            effectiveQuery.OutputSrid.Value != effectiveQuery.SpatialReferenceSrid.Value)
        {
            extentExpression = $"ST_Transform({extentExpression}, {effectiveQuery.OutputSrid.Value})";
        }

        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append(CultureInfo.InvariantCulture, $@"
            SELECT
                ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent({extentExpression}) as extent
                FROM {_tableName}
                WHERE {DatabaseSchema.LayerIdColumn} = $1 AND {extentExpression} IS NOT NULL");

            var paramIndex = 2;
            var parameters = new List<object>();

            AppendWhereClause(sql, effectiveQuery, ref paramIndex, parameters);
            AppendTemporalFilter(sql, effectiveQuery, ref paramIndex, parameters);
            AppendSpatialFilter(sql, effectiveQuery, geometryStorageType, ref paramIndex, parameters);

            sql.Append(") AS extent_query");
            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildTemporalExtentQuery(int layerId, string fieldName, FieldType fieldType)
    {
        if (!IsValidFieldName(fieldName))
        {
            throw new ArgumentException($"Invalid field name for temporal extent: {fieldName}", nameof(fieldName));
        }

        var parameters = new List<object>();
        var paramIndex = 2;
        var fieldParamIndex = paramIndex++;
        parameters.Add(fieldName);

        var attributeValue = DatabaseSchema.BuildJsonPathParameter(fieldParamIndex);
        var fieldExpression = fieldType switch
        {
            FieldType.DateTime => $"NULLIF({attributeValue}, '')::timestamptz",
            FieldType.Date => $"NULLIF({attributeValue}, '')::date",
            _ => attributeValue
        };

        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append(CultureInfo.InvariantCulture, $@"
            SELECT MIN({fieldExpression}) AS min_value, MAX({fieldExpression}) AS max_value
            FROM {_tableName}
            WHERE {DatabaseSchema.LayerIdColumn} = $1
              AND {fieldExpression} IS NOT NULL");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    public CoreParameterizedQuery BuildMvtTileQuery(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 1;
            var parameters = new List<object>();
            var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, "f.geometry", query?.SpatialReferenceSrid);
            var geometryForTile = geometryOperand;
            var filterGeometryOperand = geometryOperand;

            var tileBounds = TileMath.GetTileBounds(x, y, z);
            var tileWidth = tileBounds.XMax - tileBounds.XMin;
            var tileExtent = tileOptions.TileExtent > 0 ? tileOptions.TileExtent : 4096;
            var bufferMapUnits = tileOptions.TileBuffer > 0
                ? (tileOptions.TileBuffer / (double)tileExtent) * tileWidth
                : 0d;

            var tileEnvelope = "ST_TileEnvelope($2, $3, $4)";
            var tileEnvelopeWithBuffer = "ST_Expand(ST_TileEnvelope($2, $3, $4), $5)";

            parameters.Add(layerId);
            parameters.Add(z);
            parameters.Add(x);
            parameters.Add(y);
            parameters.Add(bufferMapUnits);
            parameters.Add(tileExtent);
            parameters.Add(tileOptions.TileBuffer);
            paramIndex = 8;

            var tileEnvelopeForFilter = tileEnvelopeWithBuffer;
            if (query.HasValue && query.Value.SpatialReferenceSrid.HasValue)
            {
                var layerSrid = query.Value.SpatialReferenceSrid.Value;
                if (layerSrid != 3857)
                {
                    geometryForTile = $"ST_Transform({geometryOperand}, 3857)";
                    tileEnvelopeForFilter = $"ST_Transform({tileEnvelopeWithBuffer}, {layerSrid})";
                }
            }
            else
            {
                geometryForTile = $"ST_Transform({geometryOperand}, 3857)";
                filterGeometryOperand = geometryForTile;
            }

            if (z <= tileOptions.SimplifyZoom)
            {
                var simplifyTolerance = TileMath.GetSimplificationTolerance(z);
                if (simplifyTolerance > 0)
                {
                    var simplifyParam = $"${paramIndex++}";
                    parameters.Add(simplifyTolerance);
                    geometryForTile = $"ST_SimplifyPreserveTopology({geometryForTile}, {simplifyParam})";
                }
            }

            sql.Append(CultureInfo.InvariantCulture, $@"
            SELECT ST_AsMVT(tile, 'layer', $6, 'geom') AS mvt
            FROM (
                SELECT
                    {DatabaseSchema.ObjectIdColumn},
                    {DatabaseSchema.AttributesColumn},
                    ST_AsMVTGeom(
                        {geometryForTile},
                        {tileEnvelope},
                        $6,
                        $7
                    ) AS geom
                FROM {_tableName} f
                WHERE {DatabaseSchema.LayerIdColumn} = $1");

            if (query != null)
            {
                AppendWhereClause(sql, (FeatureQuery)query, ref paramIndex, parameters);
                AppendTemporalFilter(sql, (FeatureQuery)query, ref paramIndex, parameters);
                AppendSpatialFilter(sql, (FeatureQuery)query, geometryStorageType, ref paramIndex, parameters);
            }

            sql.Append(CultureInfo.InvariantCulture, $@"
                    AND {filterGeometryOperand} && {tileEnvelopeForFilter}
                    AND ST_Intersects({filterGeometryOperand}, {tileEnvelopeForFilter})");

            if (tileLimits.MaxFeaturesPerTile > 0)
            {
                var limitParam = $"${paramIndex++}";
                parameters.Add(tileLimits.MaxFeaturesPerTile);
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT {limitParam}");
            }

            sql.Append(@"
                ) AS tile");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }
}
