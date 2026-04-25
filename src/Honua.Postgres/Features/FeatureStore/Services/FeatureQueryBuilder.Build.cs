// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    private const double WebMercatorEarthRadius = 6378137d;
    private const double WebMercatorMaxLatitude = 85.05112878d;

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

    private delegate void SelectClauseBuilder(
        StringBuilder sql, FeatureQuery query, CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery, SpatialFilter? spatialFilter, ref int paramIndex, List<object> parameters);

    public CoreParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
        => BuildFormatSelectQuery(query, geometryStorageType, BuildSelectClause);

    public CoreParameterizedQuery BuildProjectedPointQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var pointGeometry = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);
            var pointXExpression = "ST_X(point_geom)";
            var pointYExpression = "ST_Y(point_geom)";

            if (ShouldUseFastWebMercatorProjection(query))
            {
                pointXExpression = BuildWebMercatorXExpression("point_geom");
                pointYExpression = BuildWebMercatorYExpression("point_geom");
            }
            else if (query.OutputSrid.HasValue &&
                (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
            {
                pointGeometry = $"ST_Transform({pointGeometry}, {query.OutputSrid.Value})";
            }

            if (query.RasterPointGrid is { } pointGrid)
            {
                var originXParam = paramIndex++;
                parameters.Add(pointGrid.OriginX);
                var cellWidthParam = paramIndex++;
                parameters.Add(pointGrid.CellWidth);
                var originYParam = paramIndex++;
                parameters.Add(pointGrid.OriginY);
                var cellHeightParam = paramIndex++;
                parameters.Add(pointGrid.CellHeight);

                sql.Append(CultureInfo.InvariantCulture,
                    $"WITH point_source AS (SELECT {DatabaseSchema.ObjectIdColumn}, {pointGeometry} AS point_geom FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
                AppendWhereClause(sql, query, ref paramIndex, parameters);
                AppendTemporalFilter(sql, query, ref paramIndex, parameters);
                AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
                sql.Append("), projected_points AS (SELECT ")
                    .Append(DatabaseSchema.ObjectIdColumn)
                    .Append(", ")
                    .Append(pointXExpression)
                    .Append(" AS x, ")
                    .Append(pointYExpression)
                    .Append(" AS y FROM point_source), snapped_points AS (SELECT ")
                    .Append(DatabaseSchema.ObjectIdColumn)
                    .Append(", x, y, ");
                sql.Append(CultureInfo.InvariantCulture, $"FLOOR((x - ${originXParam}) / ${cellWidthParam})::bigint AS cell_x, ");
                sql.Append(CultureInfo.InvariantCulture, $"FLOOR((${originYParam} - y) / ${cellHeightParam})::bigint AS cell_y ");
                sql.Append("FROM projected_points) SELECT DISTINCT ON (cell_x, cell_y) x, y FROM snapped_points ORDER BY cell_x, cell_y, objectid");
            }
            else
            {
                sql.Append(CultureInfo.InvariantCulture,
                    $"WITH point_source AS (SELECT {DatabaseSchema.ObjectIdColumn}, {pointGeometry} AS point_geom FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
                AppendWhereClause(sql, query, ref paramIndex, parameters);
                AppendTemporalFilter(sql, query, ref paramIndex, parameters);
                AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
                sql.Append(") SELECT ")
                    .Append(pointXExpression)
                    .Append(" AS x, ")
                    .Append(pointYExpression)
                    .Append(" AS y FROM point_source ORDER BY objectid");
            }

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex}");
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static bool ShouldUseFastWebMercatorProjection(FeatureQuery query)
        => query.SpatialReferenceSrid == 4326 &&
           query.OutputSrid.HasValue &&
           IsWebMercatorSrid(query.OutputSrid.Value);

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    // Raster point queries are point-only call sites, so inline the same 4326->3857
    // math Honua uses in-process and avoid per-row PostGIS ST_Transform overhead.
    private static string BuildWebMercatorXExpression(string geometryExpression)
        => FormattableString.Invariant(
            $"(ST_X({geometryExpression}) * PI() / 180.0 * {WebMercatorEarthRadius.ToString(CultureInfo.InvariantCulture)})");

    private static string BuildWebMercatorYExpression(string geometryExpression)
    {
        var minLatitude = (-WebMercatorMaxLatitude).ToString(CultureInfo.InvariantCulture);
        var maxLatitude = WebMercatorMaxLatitude.ToString(CultureInfo.InvariantCulture);
        var clampedLatitude = FormattableString.Invariant(
            $"LEAST(GREATEST(ST_Y({geometryExpression}), {minLatitude}), {maxLatitude})");
        return FormattableString.Invariant(
            $"(LN(TAN((90.0 + {clampedLatitude}) * PI() / 360.0)) * {WebMercatorEarthRadius.ToString(CultureInfo.InvariantCulture)})");
    }

    public CoreParameterizedQuery BuildSelectGeoServicesPointQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var spatialFilter = query.SpatialFilter;
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var pointGeometry = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);

            if (query.OutputSrid.HasValue &&
                (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
            {
                pointGeometry = $"ST_Transform({pointGeometry}, {query.OutputSrid.Value})";
            }

            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {DatabaseSchema.AttributesColumn}::text AS {DatabaseSchema.AttributesColumn}, ST_X({pointGeometry}) AS x, ST_Y({pointGeometry}) AS y FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);
            AppendPagination(sql, isKnnQuery: false, query, spatialFilter, ref paramIndex);

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
        => BuildFormatSelectQuery(query, geometryStorageType, BuildGmlSelectClause);

    public CoreParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
        => BuildFormatSelectQuery(query, geometryStorageType, BuildGeoJsonSelectClause);

    public CoreParameterizedQuery BuildSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
        => BuildFormatSelectQuery(query, geometryStorageType, BuildKmlSelectClause);

    private CoreParameterizedQuery BuildFormatSelectQuery(
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        SelectClauseBuilder buildSelectClause)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            buildSelectClause(sql, query, geometryStorageType, isKnnQuery, spatialFilter, ref paramIndex, parameters);
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
            sql.Append(CultureInfo.InvariantCulture, $"SELECT {DatabaseSchema.ObjectIdColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            var paramIndex = 2;
            var parameters = new List<object>();

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

    public CoreParameterizedQuery BuildSelectFlatGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        return BuildEncodedBinaryQuery("ST_AsFlatGeobuf", includeIndex: true, layer, query, geometryStorageType);
    }

    public CoreParameterizedQuery BuildSelectGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        return BuildEncodedBinaryQuery("ST_AsGeobuf", includeIndex: false, layer, query, geometryStorageType);
    }

    public CoreParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);
        return BuildOptimizedSelectWithWindowCountQuery(query, geometryStorageType, geometrySelect, aliasGeometry: false);
    }

    public CoreParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);
        return BuildOptimizedSelectWithWindowCountQuery(query, geometryStorageType, geometrySelect, aliasGeometry: true);
    }

    public CoreParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);
        return BuildOptimizedSelectWithWindowCountQuery(query, geometryStorageType, geometrySelect, aliasGeometry: true);
    }

    public CoreParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var geometrySelect = _geometryProcessor.GetGeometryKmlExpression(geometryStorageType, query);
        return BuildOptimizedSelectWithWindowCountQuery(query, geometryStorageType, geometrySelect, aliasGeometry: true);
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

            var targetTileSrid = query.HasValue && query.Value.OutputSrid == 4326 ? 4326 : 3857;
            var isGeographicTile = targetTileSrid == 4326;
            var tileBounds = isGeographicTile
                ? TileMath.GetTileBoundsGeographic(x, y, z)
                : TileMath.GetTileBounds(x, y, z);
            var tileWidth = tileBounds.XMax - tileBounds.XMin;
            var tileExtent = tileOptions.TileExtent > 0 ? tileOptions.TileExtent : 4096;
            var bufferMapUnits = tileOptions.TileBuffer > 0
                ? (tileOptions.TileBuffer / (double)tileExtent) * tileWidth
                : 0d;

            var tileEnvelope = isGeographicTile
                ? BuildGeographicTileEnvelope(tileBounds)
                : "ST_TileEnvelope($2, $3, $4)";
            var tileEnvelopeWithBuffer = $"ST_Expand({tileEnvelope}, $5)";

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
                if (layerSrid != targetTileSrid)
                {
                    geometryForTile = $"ST_Transform({geometryOperand}, {targetTileSrid})";
                    tileEnvelopeForFilter = $"ST_Transform({tileEnvelopeWithBuffer}, {layerSrid})";
                }
            }
            else
            {
                geometryForTile = $"ST_Transform({geometryOperand}, {targetTileSrid})";
                filterGeometryOperand = geometryForTile;
            }

            if (!isGeographicTile && z <= tileOptions.SimplifyZoom)
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

    private static string BuildGeographicTileEnvelope(TileBounds tileBounds)
        => FormattableString.Invariant(
            $"ST_MakeEnvelope({SqlDouble(tileBounds.XMin)}, {SqlDouble(tileBounds.YMin)}, {SqlDouble(tileBounds.XMax)}, {SqlDouble(tileBounds.YMax)}, 4326)");

    private static string SqlDouble(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);
}
