// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    /// <summary>
    /// Builds an H3 hexagonal grid aggregation query.
    /// Uses h3-pg functions to convert feature geometries into H3 cell indices,
    /// then groups by cell and computes requested statistics.
    /// </summary>
    /// <remarks>
    /// H3 operates in geographic coordinates (WGS 84 / SRID 4326).
    /// Source geometries are transformed to 4326 before indexing.
    /// The query returns cell index, GeoJSON boundary, and statistics per cell.
    /// </remarks>
    public CoreParameterizedQuery BuildH3AggregationQuery(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            // Parameterize resolution for defense-in-depth consistency with tile queries
            var resolutionParam = $"${paramIndex++}";
            parameters.Add(h3Query.Resolution);

            // Build the geometry expression, transforming to WGS 84 (4326) for H3.
            // When SpatialReferenceSrid is null, geometry is assumed already in 4326.
            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var geoExpr = EnsureWgs84(geometryOperand, query.SpatialReferenceSrid);

            // H3 cell index from point geometry (centroid for non-point geometries)
            var h3CellExpr = $"h3_lat_lng_to_cell(ST_Centroid({geoExpr}), {resolutionParam})";

            // Polyfill CTE (optional) — track length so kRing wrapping can split correctly
            var polyfillCteLength = 0;
            if (h3Query.PolyfillGeometry != null)
            {
                var polyfillSrid = h3Query.PolyfillSrid ?? query.SpatialReferenceSrid ?? 4326;
                var polyfillGeomParam = $"${paramIndex++}";
                parameters.Add(h3Query.PolyfillGeometry);

                var polyfillGeom = polyfillSrid != 4326
                    ? $"ST_Transform(ST_SetSRID(ST_GeomFromWKB({polyfillGeomParam}), {polyfillSrid}), 4326)"
                    : $"ST_SetSRID(ST_GeomFromWKB({polyfillGeomParam}), 4326)";

                // NOTE: ::polygon cast converts PostGIS geometry to native PostgreSQL polygon.
                // This only works for simple Polygon geometries — MultiPolygon or
                // GeometryCollection inputs will fail at runtime. Validate geometry type
                // before calling when this path is wired to an endpoint.
                sql.Append("WITH polyfill_cells AS (");
                sql.Append(CultureInfo.InvariantCulture,
                    $"SELECT h3_polygon_to_cells({polyfillGeom}::polygon, {resolutionParam}) AS cell");
                sql.Append(')');
                polyfillCteLength = sql.Length;
                sql.Append(' ');
            }

            // Outer SELECT references pre-computed cell alias from subquery
            sql.Append("SELECT _h3cell::text AS \"cellIndex\"");
            sql.Append(", ST_AsGeoJSON(h3_cell_to_boundary(_h3cell)::geometry) AS \"cellGeometry\"");

            AppendH3StatisticsColumns(sql, h3Query);

            // Inner subquery: compute cell once per row, carry all columns for statistics
            sql.Append(CultureInfo.InvariantCulture,
                $" FROM (SELECT *, {h3CellExpr} AS _h3cell FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture,
                $" WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            // Filter out NULL geometries
            sql.Append(CultureInfo.InvariantCulture,
                $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");
            sql.Append(") _src");

            // Polyfill filter on pre-computed cell
            if (h3Query.PolyfillGeometry != null)
            {
                sql.Append(" WHERE _h3cell IN (SELECT cell FROM polyfill_cells)");
            }

            // GROUP BY pre-computed cell
            sql.Append(" GROUP BY _h3cell");

            if (h3Query.KRingDistance.HasValue && h3Query.KRingDistance.Value > 0)
            {
                // Wrap entire query to expand each result cell to its k-ring neighbors.
                // When polyfill is active, the inner SQL already starts with
                // "WITH polyfill_cells AS (...)" so we must emit comma-separated CTEs
                // instead of nesting WITH clauses (which is invalid PostgreSQL syntax).
                var innerSql = sql.ToString();
                sql.Clear();

                if (polyfillCteLength > 0)
                {
                    // Inner SQL is "WITH polyfill_cells AS (...) SELECT ...".
                    // Split at the tracked CTE boundary to produce comma-separated CTEs:
                    // WITH polyfill_cells AS (...), aggregated AS (SELECT ...)
                    var ctePrefix = innerSql[..polyfillCteLength];
                    var mainQuery = innerSql[(polyfillCteLength + 1)..]; // +1 for trailing space
                    sql.Append(ctePrefix);
                    sql.Append(", aggregated AS (");
                    sql.Append(mainQuery);
                }
                else
                {
                    sql.Append("WITH aggregated AS (");
                    sql.Append(innerSql);
                }

                var kRingParam = $"${paramIndex++}";
                parameters.Add(h3Query.KRingDistance.Value);

                // Expand each aggregated cell to its k-ring neighbors using LATERAL,
                // then GROUP BY neighbor to deduplicate cells that appear in
                // multiple source cells' neighborhoods. Statistics are re-aggregated
                // with type-appropriate functions (SUM for counts/sums, MIN/MAX
                // preserved, AVG approximated as average-of-averages).
                sql.Append(") SELECT neighbor::text AS \"cellIndex\"");
                sql.Append(", ST_AsGeoJSON(h3_cell_to_boundary(neighbor)::geometry) AS \"cellGeometry\"");
                AppendH3KRingReaggregate(sql, h3Query);
                sql.Append(CultureInfo.InvariantCulture,
                    $" FROM aggregated a, LATERAL h3_grid_disk(a.\"cellIndex\"::h3index, {kRingParam}) AS neighbor");
                sql.Append(" GROUP BY neighbor");

                // Apply max-cells limit to the kRing-expanded result
                if (h3Query.MaxCells.HasValue && h3Query.MaxCells.Value > 0)
                {
                    var maxCellsParam = $"${paramIndex++}";
                    parameters.Add(h3Query.MaxCells.Value);
                    sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxCellsParam}");
                }
            }
            else
            {
                // ORDER BY count descending for useful default ordering
                // (only on the non-k-ring path; inside a CTE it would be ignored by PostgreSQL)
                sql.Append(" ORDER BY COUNT(*) DESC");

                // Apply max-cells limit to prevent unbounded result sets at high resolutions
                if (h3Query.MaxCells.HasValue && h3Query.MaxCells.Value > 0)
                {
                    var maxCellsParam = $"${paramIndex++}";
                    parameters.Add(h3Query.MaxCells.Value);
                    sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxCellsParam}");
                }
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Builds an MVT tile query that renders H3 cell boundaries as vector tile features.
    /// Resolution is selected explicitly rather than derived from zoom.
    /// </summary>
    public CoreParameterizedQuery BuildH3TileQuery(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        Core.Features.Tiles.TileOptions tileOptions,
        Core.Configuration.TileLimits tileLimits,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 1;
            var parameters = new List<object>();

            var tileExtent = tileOptions.TileExtent > 0 ? tileOptions.TileExtent : 4096;
            var tileBounds = TileMath.GetTileBounds(x, y, z);
            var tileWidth = tileBounds.XMax - tileBounds.XMin;
            var bufferMapUnits = tileOptions.TileBuffer > 0
                ? (tileOptions.TileBuffer / (double)tileExtent) * tileWidth
                : 0d;

            parameters.Add(layerId);     // $1
            parameters.Add(z);           // $2
            parameters.Add(x);           // $3
            parameters.Add(y);           // $4
            parameters.Add(bufferMapUnits); // $5
            parameters.Add(tileExtent);  // $6
            parameters.Add(tileOptions.TileBuffer); // $7
            parameters.Add(resolution);  // $8
            paramIndex = 9;

            var tileEnvelope = "ST_TileEnvelope($2, $3, $4)";
            var tileEnvelopeWithBuffer = "ST_Expand(ST_TileEnvelope($2, $3, $4), $5)";

            // Build source geometry expression, transforming to WGS 84 (4326) for H3.
            // When SpatialReferenceSrid is null, geometry is assumed already in 4326.
            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, "f.geometry", query?.SpatialReferenceSrid);
            var geoExpr = EnsureWgs84(geometryOperand, query?.SpatialReferenceSrid);

            // H3 cell computed once per row in inner subquery
            var h3CellExpr = $"h3_lat_lng_to_cell(ST_Centroid({geoExpr}), $8)";

            // Filter envelope in source SRID for index use.
            // When the layer SRID is known and differs from Web Mercator (3857),
            // transform the tile envelope into the layer's CRS so the && bbox
            // filter can use the spatial index. When SRID is unknown, assume
            // WGS 84 (consistent with EnsureWgs84) and transform accordingly.
            var tileEnvelopeForFilter = tileEnvelopeWithBuffer;
            if (query.HasValue && query.Value.SpatialReferenceSrid.HasValue)
            {
                var layerSrid = query.Value.SpatialReferenceSrid.Value;
                if (layerSrid != 3857)
                {
                    tileEnvelopeForFilter = $"ST_Transform({tileEnvelopeWithBuffer}, {layerSrid})";
                }
            }
            else
            {
                // No SRID available — EnsureWgs84 assumes 4326, so transform
                // the tile envelope to match for a correct bbox comparison.
                tileEnvelopeForFilter = $"ST_Transform({tileEnvelopeWithBuffer}, 4326)";
            }

            // Outer MVT aggregation; middle layer groups by cell; inner subquery computes cell once
            sql.Append(CultureInfo.InvariantCulture, $@"
            SELECT ST_AsMVT(tile, 'h3', $6, 'geom') AS mvt
            FROM (
                SELECT
                    _h3cell::text AS cell_index,
                    COUNT(*) AS feature_count,
                    ST_AsMVTGeom(
                        ST_Transform(h3_cell_to_boundary(_h3cell)::geometry, 3857),
                        {tileEnvelope},
                        $6,
                        $7
                    ) AS geom
                FROM (
                    SELECT {h3CellExpr} AS _h3cell
                    FROM {_tableName} f
                    WHERE {DatabaseSchema.LayerIdColumn} = $1");

            sql.Append(CultureInfo.InvariantCulture,
                $" AND f.{DatabaseSchema.GeometryColumn} IS NOT NULL");
            sql.Append(CultureInfo.InvariantCulture,
                $" AND f.{DatabaseSchema.GeometryColumn} && {tileEnvelopeForFilter}");

            if (query.HasValue)
            {
                AppendWhereClause(sql, query.Value, ref paramIndex, parameters);
                AppendTemporalFilter(sql, query.Value, ref paramIndex, parameters);
                AppendSpatialFilter(sql, query.Value, geometryStorageType, ref paramIndex, parameters);
            }

            sql.Append(") _src");
            sql.Append(CultureInfo.InvariantCulture,
                $" GROUP BY _h3cell");

            // Limit the number of aggregated H3 cells per tile (not source rows).
            // Applied after GROUP BY so that counts per cell remain accurate.
            if (tileLimits.MaxFeaturesPerTile > 0)
            {
                var limitParam = $"${paramIndex++}";
                parameters.Add(tileLimits.MaxFeaturesPerTile);
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT {limitParam}");
            }

            sql.Append(") AS tile WHERE geom IS NOT NULL");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Ensures a geometry expression is in WGS 84 (SRID 4326) for H3 operations.
    /// When <paramref name="sourceSrid"/> is null (layer has no defined spatial reference),
    /// the geometry is assumed to already be in WGS 84. Layers stored in a different CRS
    /// without SRID metadata will produce incorrect H3 cell assignments — this is an
    /// operator configuration issue, not a runtime-recoverable error.
    /// </summary>
    private static string EnsureWgs84(string geometryOperand, int? sourceSrid)
    {
        if (sourceSrid.HasValue && sourceSrid.Value != 4326)
        {
            return $"ST_Transform({geometryOperand}, 4326)";
        }

        // SRID is null (unknown) or already 4326 — pass through unchanged.
        // Null-SRID layers are assumed WGS 84 by convention; misconfigured
        // layers (non-4326 data without SRID) will index into wrong H3 cells.
        return geometryOperand;
    }

    private static void AppendH3StatisticsColumns(System.Text.StringBuilder sql, H3AggregationQuery h3Query)
    {
        if (h3Query.OutStatistics.HasValue && !h3Query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            foreach (var stat in h3Query.OutStatistics.Value)
            {
                if (!IsValidFieldName(stat.OnStatisticField))
                {
                    throw new ArgumentException($"Invalid statistic field name: {stat.OnStatisticField}");
                }

                if (!IsValidFieldName(stat.OutStatisticFieldName))
                {
                    throw new ArgumentException($"Invalid output statistic field name: {stat.OutStatisticFieldName}");
                }

                var statFieldExpr = GetFieldExpression(stat.OnStatisticField);
                var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr);
                sql.Append(CultureInfo.InvariantCulture,
                    $", {aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
            }
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $", COUNT({DatabaseSchema.ObjectIdColumn}) AS \"count\"");
        }
    }

    /// <summary>
    /// Appends re-aggregation expressions for the kRing outer GROUP BY.
    /// Each statistic from the inner aggregation is re-aggregated with a
    /// type-appropriate function so that overlapping neighbor cells are
    /// merged correctly rather than producing duplicate rows.
    /// </summary>
    private static void AppendH3KRingReaggregate(System.Text.StringBuilder sql, H3AggregationQuery h3Query)
    {
        if (h3Query.OutStatistics.HasValue && !h3Query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            foreach (var stat in h3Query.OutStatistics.Value)
            {
                var outerAgg = GetKRingReaggregateFunction(stat.StatisticType);
                var alias = SanitizeAlias(stat.OutStatisticFieldName);
                sql.Append(CultureInfo.InvariantCulture,
                    $", {outerAgg}(a.{alias}) AS {alias}");
            }
        }
        else
        {
            sql.Append(", SUM(a.\"count\") AS \"count\"");
        }
    }

    /// <summary>
    /// Maps an inner-aggregation statistic type to the appropriate outer
    /// re-aggregation function for kRing deduplication.
    /// </summary>
    private static string GetKRingReaggregateFunction(StatisticType statisticType) => statisticType switch
    {
        StatisticType.Count => "SUM",
        StatisticType.Sum => "SUM",
        StatisticType.Min => "MIN",
        StatisticType.Max => "MAX",
        // Average-of-averages is an approximation; exact weighted average
        // would require carrying per-cell counts which adds query complexity.
        // For the visualization-oriented kRing use case this is acceptable.
        StatisticType.Avg => "AVG",
        // Stddev/Var: AVG-of-stddevs (or variances) is NOT a valid pooled
        // statistic — the correct formula needs per-cell counts and means.
        // Used here as a rough central-tendency proxy for visualization only.
        StatisticType.Stddev => "AVG",
        StatisticType.Var => "AVG",
        _ => "SUM"
    };
}
