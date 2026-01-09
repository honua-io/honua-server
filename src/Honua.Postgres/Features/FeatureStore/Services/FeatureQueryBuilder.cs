// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Builds SQL queries for PostgreSQL feature store operations
/// </summary>
internal sealed class FeatureQueryBuilder : IFeatureQueryBuilder
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18";

    private static readonly Regex _comparisonRegex = new(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _nullCheckRegex = new(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _trueLiteralRegex = new(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex);
            AppendOrderByClause(sql, query);
            AppendKnnOrdering(sql, isKnnQuery, spatialFilter, query, geometryStorageType, ref paramIndex);
            AppendPagination(sql, isKnnQuery, query, spatialFilter, ref paramIndex);

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
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex);
            AppendOrderByClause(sql, query);
            AppendKnnOrdering(sql, isKnnQuery, spatialFilter, query, geometryStorageType, ref paramIndex);
            AppendPagination(sql, isKnnQuery, query, spatialFilter, ref paramIndex);

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
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
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex);

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
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex);
            AppendOrderByClause(sql, query);

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
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex);
            AppendOrderByClause(sql, query);

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
            AppendSpatialFilter(sql, effectiveQuery, geometryStorageType, ref paramIndex);

            sql.Append(") AS extent_query");
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
        string? tileBuffer = null,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 1;
            var parameters = new List<object>();
            var buffer = string.IsNullOrEmpty(tileBuffer) ? "ST_TileEnvelope($2, $3, $4)" : tileBuffer;

            var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, "f.geometry", query?.SpatialReferenceSrid);
            if (query.HasValue && query.Value.SpatialReferenceSrid.HasValue &&
                query.Value.SpatialReferenceSrid.Value != 3857)
            {
                geometryOperand = $"ST_Transform({geometryOperand}, 3857)";
            }

            sql.Append(CultureInfo.InvariantCulture, $@"
            SELECT ST_AsMVT(tile, 'layer', 4096, 'geom') AS mvt
            FROM (
                SELECT
                    {DatabaseSchema.ObjectIdColumn},
                    {DatabaseSchema.AttributesColumn},
                    ST_AsMVTGeom(
                        {geometryOperand},
                        {buffer}
                    ) AS geom
                FROM {_tableName} f
                WHERE {DatabaseSchema.LayerIdColumn} = ${paramIndex++}");

            parameters.Add(layerId);
            parameters.Add(z);
            parameters.Add(x);
            parameters.Add(y);
            paramIndex = 5;

            if (query != null)
            {
                AppendWhereClause(sql, (FeatureQuery)query, ref paramIndex, parameters);
                AppendTemporalFilter(sql, (FeatureQuery)query, ref paramIndex, parameters);
                AppendSpatialFilter(sql, (FeatureQuery)query, geometryStorageType, ref paramIndex);
            }

            sql.Append(CultureInfo.InvariantCulture, $@"
                    AND ST_Intersects({geometryOperand}, ST_TileEnvelope($2, $3, $4))
                ) AS tile");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private void BuildSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = ((GeometryProcessor)_geometryProcessor).GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as distance FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = ((GeometryProcessor)_geometryProcessor).GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as distance FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private string BuildGeographyFilterExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex)
    {
        var geometryExpression = _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);

        var wgs84Srid = SpatialReference.WGS84.Wkid;
        if (query.SpatialReferenceSrid.HasValue && query.SpatialReferenceSrid.Value != wgs84Srid)
        {
            geometryExpression = $"ST_Transform({geometryExpression}, {wgs84Srid})";
        }

        return $"{geometryExpression}::geography";
    }

    private static void AppendWhereClause(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        // Prefer SqlFragment if available (CQL2 filters with proper parameterization)
        if (query.SqlFilter != null)
        {
            var sqlFragment = query.SqlFilter;

            // Convert @p0, @p1, etc. to positional $N, $N+1, etc. parameters
            var convertedSql = ConvertNamedParametersToPositional(sqlFragment.Sql, ref paramIndex);

            // Append the converted SQL
            sql.Append(CultureInfo.InvariantCulture, $" AND ({convertedSql})");

            foreach (var param in sqlFragment.Parameters)
            {
                parameters.Add(param ?? DBNull.Value);
            }
        }
        // Fall back to legacy string WHERE clause for backward compatibility
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var whereClause = query.Where.Trim();

            // Parse and parameterize simple WHERE clauses
            // Supports: field = 'value', field > 123, field LIKE 'pattern%'
            var parameterizedClause = ParseAndParameterizeWhereClause(whereClause, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $" AND ({parameterizedClause})");
        }

        if (query.ObjectIds.HasValue && !query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            var objectIds = query.ObjectIds.Value;
            var placeholders = new string[objectIds.Length];

            for (var i = 0; i < objectIds.Length; i++)
            {
                placeholders[i] = $"${paramIndex + i}";
            }

            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.ObjectIdColumn} = ANY(ARRAY[{string.Join(", ", placeholders)}])");

            foreach (var objectId in objectIds)
            {
                parameters.Add(objectId);
            }

            paramIndex += objectIds.Length;
        }
    }

    private static void AppendTemporalFilter(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        if (!query.TemporalFilter.HasValue)
        {
            return;
        }

        var filter = query.TemporalFilter.Value;
        var fieldName = filter.PropertyName;
        var valueExpression = filter.PropertyType switch
        {
            TemporalPropertyType.Date => $"NULLIF({DatabaseSchema.BuildJsonPath(fieldName)}, '')::date",
            _ => $"NULLIF({DatabaseSchema.BuildJsonPath(fieldName)}, '')::timestamptz"
        };

        string? predicate = null;

        if (filter.Start.HasValue && filter.End.HasValue)
        {
            var startIndex = paramIndex++;
            var endIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);
            parameters.Add(filter.End.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            predicate = $"{valueExpression} >= {startExpr} AND {valueExpression} <= {endExpr}";
        }
        else if (filter.Start.HasValue)
        {
            var startIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            predicate = $"{valueExpression} >= {startExpr}";
        }
        else if (filter.End.HasValue)
        {
            var endIndex = paramIndex++;
            parameters.Add(filter.End.Value.UtcDateTime);

            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            predicate = $"{valueExpression} <= {endExpr}";
        }

        if (predicate is null)
        {
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $" AND {predicate}");
    }

    private void AppendSpatialFilter(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        ref int paramIndex)
    {
        if (!query.SpatialFilter.HasValue)
        {
            return;
        }

        var filter = query.SpatialFilter.Value;
        var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);
        var geographyOperand = ((GeometryProcessor)_geometryProcessor).GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
        string? filterGeometry = null;

        switch (filter.SpatialRelationship)
        {
            case SpatialRelationship.Intersects:
                // PERFORMANCE OPTIMIZATION: Use bbox operator && for fast spatial index filtering
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Intersects({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Within:
                // PERFORMANCE OPTIMIZATION: Pre-filter with bbox before expensive ST_Within
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Within({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Contains:
                // PERFORMANCE OPTIMIZATION: Use spatial index hint for containment queries
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Contains({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.EnvelopeIntersects:
                // Already optimized - pure index operation
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry}");
                break;

            case SpatialRelationship.Crosses:
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Crosses({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Touches:
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Touches({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Overlaps:
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Overlaps({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Disjoint:
                // PERFORMANCE NOTE: Disjoint operations cannot effectively use spatial indexes
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND ST_Disjoint({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Equals:
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Equals({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.WithinDistance:
                // Use ST_DWithin with geography type for accurate geodesic distance calculations
                var geographyFilter = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND ST_DWithin({geographyOperand}, {geographyFilter}, ${paramIndex++})");
                break;

            case SpatialRelationship.BeyondDistance:
                // ST_Distance > threshold for features beyond a certain distance
                var geographyFilterDistance = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND ST_Distance({geographyOperand}, {geographyFilterDistance}) > ${paramIndex++}");
                break;

            case SpatialRelationship.NearestNeighbor:
                // KNN uses ORDER BY with PostGIS <-> operator (handled separately)
                sql.Append(CultureInfo.InvariantCulture, $" AND {geometryOperand} IS NOT NULL");
                break;

            default:
                // PERFORMANCE OPTIMIZATION: Default to bbox + intersects for best performance
                filterGeometry ??= _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {geometryOperand} && {filterGeometry} AND ST_Intersects({geometryOperand}, {filterGeometry})");
                break;
        }
    }

    private static void AppendOrderByClause(StringBuilder sql, FeatureQuery query)
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
            var fieldSql = MapOrderByField(orderBy);
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            orderClauses.Add($"{fieldSql} {direction}");
        }

        if (orderClauses.Count > 0)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", orderClauses));
        }
    }

    private void AppendKnnOrdering(
        StringBuilder sql,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        ref int paramIndex)
    {
        if (!isKnnQuery || !spatialFilter.HasValue)
        {
            return;
        }

        if (ShouldUseGeodesicKnn(geometryStorageType, query))
        {
            var geographyOperand = ((GeometryProcessor)_geometryProcessor).GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var filterGeometry = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY ST_Distance({geographyOperand}, {filterGeometry})");
            return;
        }

        var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);
        var filterGeometryPlanar = _geometryProcessor.BuildSpatialFilterGeometryExpression(spatialFilter.Value, query, ref paramIndex);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {geometryOperand} <-> {filterGeometryPlanar}");
    }

    private static void AppendPagination(StringBuilder sql, bool isKnnQuery, FeatureQuery query, SpatialFilter? spatialFilter, ref int paramIndex)
    {
        if (isKnnQuery)
        {
            // For KNN, use NearestCount as LIMIT if specified, otherwise use regular Limit
            var limit = spatialFilter?.NearestCount ?? query.Limit;
            if (limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
            }
        }
        else if (query.Limit.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
        }

        if (query.Offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
        }
    }

    private static string MapOrderByField(OrderByClause orderBy)
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

        if (orderBy.FieldType.HasValue)
        {
            var attributeValue = DatabaseSchema.BuildJsonPath(fieldName);
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

        return DatabaseSchema.BuildJsonPath(fieldName);
    }

    internal static bool IsValidFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return Regex.IsMatch(fieldName, @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);
    }

    private static string ConvertNamedParametersToPositional(string sql, ref int paramIndex)
    {
        var startingParamIndex = paramIndex;

        var result = Regex.Replace(
            sql,
            @"@p(\d+)",
            match =>
            {
                var paramNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return $"${startingParamIndex + paramNumber}";
            });

        var maxParamNumber = Regex.Matches(sql, @"@p(\d+)")
            .Cast<Match>()
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(-1)
            .Max();

        if (maxParamNumber >= 0)
        {
            paramIndex = startingParamIndex + maxParamNumber + 1;
        }

        return result;
    }

    internal static string ParseAndParameterizeWhereClause(string whereClause, ref int paramIndex, List<object> parameters)
    {
        var dangerousPattern = FindDangerousPattern(whereClause);
        if (dangerousPattern != null)
        {
            throw new ArgumentException($"WHERE clause contains dangerous pattern: {dangerousPattern}");
        }

        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        var parameterizedExpressions = new List<string>(expressions.Count);

        foreach (var expression in expressions)
        {
            var trimmedExpression = expression.Trim();
            if (trimmedExpression.Length == 0)
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            if (_trueLiteralRegex.IsMatch(trimmedExpression))
            {
                parameterizedExpressions.Add("TRUE");
                continue;
            }

            var nullMatch = _nullCheckRegex.Match(trimmedExpression);
            if (nullMatch.Success)
            {
                var fieldName = nullMatch.Groups["field"].Value;
                var fieldSql = MapWhereField(fieldName, out _);
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"{fieldSql} IS {notClause}NULL");
                continue;
            }

            var comparisonMatch = _comparisonRegex.Match(trimmedExpression);
            if (comparisonMatch.Success)
            {
                var fieldName = comparisonMatch.Groups["field"].Value;
                var operatorValue = comparisonMatch.Groups["op"].Value;
                var valueToken = comparisonMatch.Groups["value"].Value;

                var fieldSql = MapWhereField(fieldName, out var isAttributeField);
                var normalizedOperator = NormalizeOperator(operatorValue);
                var forceTextComparison = normalizedOperator.Contains("LIKE", StringComparison.OrdinalIgnoreCase);

                var parameterizedValue = ParseValueToken(valueToken, forceTextComparison || isAttributeField);
                parameterizedExpressions.Add($"{fieldSql} {normalizedOperator} ${paramIndex}");
                parameters.Add(parameterizedValue);
                paramIndex++;
            }
            else
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }
        }

        return string.Join(" AND ", parameterizedExpressions);
    }

    private static string MapWhereField(string fieldName, out bool isAttributeField)
    {
        var jsonPathIndex = fieldName.IndexOf("->>", StringComparison.Ordinal);
        if (jsonPathIndex >= 0)
        {
            var baseField = fieldName[..jsonPathIndex];
            if (!baseField.Equals(DatabaseSchema.AttributesColumn, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            isAttributeField = true;
            return $"{DatabaseSchema.AttributesColumn}{fieldName[jsonPathIndex..]}";
        }

        isAttributeField = fieldName.ToLowerInvariant() switch
        {
            DatabaseSchema.ObjectIdColumn => false,
            DatabaseSchema.ObjectIdColumnAlt => false,
            DatabaseSchema.LayerIdColumnAlt => false,
            DatabaseSchema.LayerIdColumn => false,
            DatabaseSchema.GeometryColumn => false,
            "created_at" => false,
            "updated_at" => false,
            _ => true
        };

        return fieldName.ToLowerInvariant() switch
        {
            DatabaseSchema.ObjectIdColumn => DatabaseSchema.ObjectIdColumn,
            DatabaseSchema.ObjectIdColumnAlt => DatabaseSchema.ObjectIdColumn,
            DatabaseSchema.LayerIdColumnAlt => DatabaseSchema.LayerIdColumn,
            DatabaseSchema.LayerIdColumn => DatabaseSchema.LayerIdColumn,
            DatabaseSchema.GeometryColumn => DatabaseSchema.GeometryColumn,
            "created_at" => "created_at",
            "updated_at" => "updated_at",
            _ => $"({DatabaseSchema.BuildJsonPath(fieldName)})"
        };
    }

    private static string NormalizeOperator(string operatorValue)
    {
        return operatorValue.Replace("<>", "!=", StringComparison.OrdinalIgnoreCase);
    }

    private static object ParseValueToken(string valueToken, bool forceText)
    {
        if (valueToken.StartsWith('\'') && valueToken.EndsWith('\''))
        {
            return UnescapeSqlString(valueToken);
        }

        if (!forceText && decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        return valueToken;
    }

    private static string UnescapeSqlString(string valueToken)
    {
        var content = valueToken[1..^1]; // Remove surrounding quotes
        return content.Replace("''", "'"); // Unescape single quotes
    }

    private static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];

            if (!inQuotes && (c == '\'' || c == '"'))
            {
                inQuotes = true;
                quoteChar = c;
                current.Append(c);
            }
            else if (inQuotes && c == quoteChar)
            {
                if (i + 1 < whereClause.Length && whereClause[i + 1] == quoteChar)
                {
                    current.Append(c);
                    current.Append(c);
                    i++;
                }
                else
                {
                    inQuotes = false;
                    quoteChar = '\0';
                    current.Append(c);
                }
            }
            else if (!inQuotes && IsAndTokenAt(whereClause, i))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2; // Skip "AND"
                while (i < whereClause.Length && char.IsWhiteSpace(whereClause[i]))
                {
                    i++;
                }
                i--; // Compensate for the loop increment
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static bool IsAndTokenAt(string whereClause, int index)
    {
        if (index + 3 > whereClause.Length)
        {
            return false;
        }

        var candidate = whereClause.Substring(index, 3);
        if (!candidate.Equals("AND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prevChar = index > 0 ? whereClause[index - 1] : ' ';
        var nextChar = index + 3 < whereClause.Length ? whereClause[index + 3] : ' ';

        return !IsIdentifierChar(prevChar) && !IsIdentifierChar(nextChar);
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static bool ShouldUseGeodesicKnn(CoreGeometryStorageType geometryStorageType, FeatureQuery query)
    {
        if (geometryStorageType == CoreGeometryStorageType.Geography)
        {
            return true;
        }

        return query.SpatialReferenceSrid.HasValue && query.SpatialReferenceSrid.Value == SpatialReference.WGS84.Wkid;
    }

    private static string? FindDangerousPattern(string whereClause)
    {
        var dangerousPatterns = new[]
        {
            "UNION", "SELECT", "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER",
            "EXEC", "EXECUTE", "xp_", "sp_", "INFORMATION_SCHEMA", "--", "/*", "*/", ";"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (ContainsOutsideQuotes(whereClause, pattern))
            {
                return pattern;
            }
        }

        return null;
    }

    private static bool ContainsOutsideQuotes(string input, string pattern)
    {
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i <= input.Length - pattern.Length; i++)
        {
            var c = input[i];

            if (!inQuotes && (c == '\'' || c == '"'))
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (inQuotes && c == quoteChar)
            {
                if (i + 1 < input.Length && input[i + 1] == quoteChar)
                {
                    i++;
                }
                else
                {
                    inQuotes = false;
                    quoteChar = '\0';
                }
            }
            else if (!inQuotes)
            {
                var substring = input.Substring(i, Math.Min(pattern.Length, input.Length - i));
                if (substring.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var beforeChar = i > 0 ? input[i - 1] : ' ';
                    var afterChar = i + pattern.Length < input.Length ? input[i + pattern.Length] : ' ';

                    if (!IsIdentifierChar(beforeChar) && !IsIdentifierChar(afterChar))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
