// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Spatial analytics SQL builders — clustering, buffer aggregate and density binning.
/// Spatial-join SQL lives in <see cref="FeatureQueryBuilder"/>.Analytics.Joins.cs.
/// All builders honour the shared <see cref="FeatureQuery"/> filter via the existing
/// <c>AppendWhereClause</c> / <c>AppendTemporalFilter</c> / <c>AppendSpatialFilter</c>
/// helpers, and emit positional parameters consistent with the rest of the file.
/// </summary>
internal sealed partial class FeatureQueryBuilder
{
    /// <summary>
    /// Builds a spatial clustering query (DBSCAN or K-Means).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both algorithms use a window function over the bbox-filtered subset so the
    /// clustering decision is made in a single SQL pass. DBSCAN uses
    /// <c>ST_ClusterDBSCAN(geom, eps, minpoints) OVER ()</c>, K-Means uses
    /// <c>ST_ClusterKMeans(geom, k) OVER ()</c>.
    /// </para>
    /// <para>
    /// When the layer SRID is geographic the geometry is projected to an azimuthal
    /// equidistant plane (WGS 84, metres) centred on the request envelope, or on
    /// the centroid of the filtered extent when the request has no envelope.
    /// <c>eps</c> is then ground metres at that centre. Scale error grows with
    /// distance from the centre. An envelope wider than 180° uses the centre of
    /// the complementary arc so a dateline-crossing window is not centred on the
    /// long way around. A filtered extent with no request envelope uses the box
    /// centroid, which is the long-way centre when the rows themselves straddle
    /// the antimeridian. <c>ST_ClusterDBSCAN</c> cannot take a geography distance.
    /// Projected layers keep the Web Mercator planar path, including foot CRS
    /// values converted by <c>ST_Transform</c> to EPSG:3857.
    /// </para>
    /// <para>
    /// The CTE applies <c>LIMIT (maxInputFeatures + 1)</c> so the handler can
    /// detect overflow without scanning the entire table.
    /// </para>
    /// </remarks>
    public CoreParameterizedQuery BuildClusterQuery(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(query, "cluster");
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var geographic = IsGeographicLayer(query.SpatialReferenceSrid);
            var metersGeometry = geographic
                ? $"ST_TransformPipeline(geom, {BuildAeqdPipelineSql(query, query.SpatialReferenceSrid!.Value, "filtered", "geom", inverse: false)})"
                : EnsureMeters(geometryOperand, query.SpatialReferenceSrid);

            string clusterExpression;
            if (clusterQuery.Algorithm == ClusterAlgorithm.DbScan)
            {
                var epsValue = _geometryProcessor.ConvertDistanceToMeters(
                    clusterQuery.Eps ?? 0d, clusterQuery.DistanceUnit);
                var epsParam = $"${paramIndex++}";
                parameters.Add(epsValue);

                var minPointsParam = $"${paramIndex++}";
                parameters.Add(clusterQuery.MinPoints ?? 1);

                clusterExpression = FormattableString.Invariant(
                    $"ST_ClusterDBSCAN({metersGeometry}, eps => {epsParam}, minpoints => {minPointsParam}) OVER ()");
            }
            else
            {
                var kParam = $"${paramIndex++}";
                parameters.Add(clusterQuery.K ?? 1);
                clusterExpression = FormattableString.Invariant(
                    $"ST_ClusterKMeans({metersGeometry}, {kParam}) OVER ()");
            }

            // Store the decoded, storage-aware geometry under the alias `geom` so the
            // outer SELECT does not reference the raw `geometry` column. In bytea
            // storage mode `ST_AsGeoJSON(bytea)` / `ST_Collect(bytea)` would fail at
            // execution; carrying the typed operand through the CTE keeps the output
            // path correct across Geometry / Geography / Bytea storage modes.
            // Field masks (#1940) are subtracted where the attributes enter the CTE, so
            // neither the per-feature "attributes" output nor the per-cluster statistics
            // can observe a masked value. Without masks this is the bare column.
            var attributesSource = BuildMaskedAttributesColumn(ResolveMaskedFields(query), ref paramIndex, parameters);

            var maxInputParam = $"${paramIndex++}";
            parameters.Add(clusterQuery.MaxInputFeatures + 1);

            if (geographic)
            {
                // Filter first so the azimuthal centre can be the extent of the
                // same rows the cluster sees, then project those rows.
                sql.Append("WITH filtered AS (SELECT ");
                AppendClusterSourceColumns(sql, attributesSource, geometryOperand, trailingComma: false);
                AppendClusterSourceFrom(sql, query, geometryStorageType, ref paramIndex, parameters, maxInputParam);
                sql.Append(", src AS (SELECT ");
                sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
                sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.AttributesColumn}, geom, ");
                sql.Append(CultureInfo.InvariantCulture, $"{clusterExpression} AS cluster_id FROM filtered)");
            }
            else
            {
                sql.Append("WITH src AS (SELECT ");
                AppendClusterSourceColumns(sql, attributesSource, geometryOperand, trailingComma: true);
                sql.Append(CultureInfo.InvariantCulture, $"{clusterExpression} AS cluster_id");
                AppendClusterSourceFrom(sql, query, geometryStorageType, ref paramIndex, parameters, maxInputParam);
            }

            if (clusterQuery.ReturnHullPerCluster)
            {
                // Aggregated mode — one row per cluster with the convex hull and stats.
                // _inputCount carries the actual src CTE row count (capped at
                // MaxInputFeatures+1) so the handler can report InputTruncated even
                // though COUNT(*) per cluster does not include DBSCAN noise points.
                // The hull is reprojected to EPSG:4326 before serialisation so the
                // GeoJSON response is consistent with application/geo+json's
                // WGS 84 contract regardless of the layer's stored SRID.
                sql.Append(" SELECT (SELECT COUNT(*)::bigint FROM src) AS \"_inputCount\"");
                sql.Append(", cluster_id::bigint AS \"clusterId\"");
                sql.Append(", COUNT(*)::bigint AS \"featureCount\"");
                sql.Append(", ST_AsGeoJSON(ST_Transform(ST_ConvexHull(ST_Collect(geom)), 4326)) AS \"clusterGeometry\"");
                AppendClusterStatisticsColumns(sql, clusterQuery.OutStatistics);
                sql.Append(" FROM src WHERE cluster_id IS NOT NULL");
                sql.Append(" GROUP BY cluster_id ORDER BY \"featureCount\" DESC");
                if (clusterQuery.MaxClusters > 0)
                {
                    var maxClustersParam = $"${paramIndex}";
                    parameters.Add(clusterQuery.MaxClusters + 1);
                    sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxClustersParam}");
                }
            }
            else
            {
                // Per-feature mode — one row per source feature carrying its assigned
                // cluster id. The geometry is reprojected to EPSG:4326 to match the
                // GeoJSON contract; layer-native SRIDs would otherwise leak through
                // application/geo+json responses.
                sql.Append(CultureInfo.InvariantCulture,
                    $" SELECT {DatabaseSchema.ObjectIdColumn} AS \"objectId\"");
                sql.Append(", cluster_id::bigint AS \"clusterId\"");
                sql.Append(CultureInfo.InvariantCulture,
                    $", {DatabaseSchema.AttributesColumn} AS \"attributes\"");
                sql.Append(", ST_AsGeoJSON(ST_Transform(geom, 4326)) AS \"geometry\"");
                sql.Append(" FROM src ORDER BY cluster_id NULLS LAST, \"objectId\"");
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Builds a buffer-aggregate query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Buffers each input feature by <see cref="BufferAggregateQuery.Distance"/>
    /// (converted to meters), then either <c>ST_Union</c>s the buffered geometries
    /// per group (when <see cref="BufferAggregateQuery.Dissolve"/> is true) or
    /// returns the per-row buffers. The buffer is computed in Web Mercator and
    /// the resulting geometry is emitted as GeoJSON in WGS 84 to match the rest
    /// of the analytics surface.
    /// </para>
    /// <para>
    /// The input cap is applied inside a <c>src</c> CTE so <c>ST_Buffer</c> /
    /// <c>ST_Union</c> only see at most <see cref="BufferAggregateQuery.MaxInputFeatures"/>
    /// rows. A trailing <c>LIMIT</c> on the outer query would not fire until after
    /// the group aggregation completes, which in the <c>dissolve=true</c> path
    /// defeats the purpose of the cap — a large layer could still force an
    /// unbounded expensive union. Pre-decoding the meters geometry inside the CTE
    /// also means the storage-aware operand (bytea / geography / geometry) is
    /// evaluated once and the outer query can reuse a single <c>geom_m</c>
    /// expression.
    /// </para>
    /// </remarks>
    public CoreParameterizedQuery BuildBufferAggregateQuery(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(query, "buffer-aggregate");
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var distanceMeters = _geometryProcessor.ConvertDistanceToMeters(
                bufferQuery.Distance, bufferQuery.Unit);
            var distanceParam = $"${paramIndex++}";
            parameters.Add(distanceMeters);

            var groupByFields = bufferQuery.GroupByFields;
            var hasGroupBy = groupByFields.HasValue && !groupByFields.Value.IsDefaultOrEmpty;

            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var metersGeometry = EnsureMeters(geometryOperand, query.SpatialReferenceSrid);

            var bufferedGeometry = $"ST_Buffer(geom_m, {distanceParam})";

            // Source CTE — decode the meters geometry once and bound the input set
            // BEFORE the buffer/union aggregation. objectid and attributes are carried
            // forward so the outer query can reference them through AppendGroupBy* /
            // AppendClusterStatisticsColumns without hitting the base table again.
            sql.Append("WITH src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.AttributesColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{metersGeometry} AS geom_m");
            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            // LIMIT n+1 inside the CTE so ST_Buffer / ST_Union only operate on the
            // capped input set and the handler can still detect overflow without
            // letting the query scan beyond MaxInputFeatures.
            var maxInputParam = $"${paramIndex}";
            parameters.Add(bufferQuery.MaxInputFeatures + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam})");

            sql.Append(" SELECT ");

            // _inputCount carries the actual src CTE row count (capped at
            // MaxInputFeatures+1) so the handler can report InputTruncated in the
            // dissolve=true path where COUNT(*) per group does not represent the
            // pre-cap input volume. Per-feature mode still emits it for symmetry
            // (each per-feature row knows its own input count is rows.Length).
            sql.Append("(SELECT COUNT(*)::bigint FROM src) AS \"_inputCount\", ");

            // Group-by columns first.
            if (hasGroupBy)
            {
                AppendGroupByFieldSelectList(sql, groupByFields!.Value);
                sql.Append(", ");
            }

            if (bufferQuery.Dissolve)
            {
                sql.Append("COUNT(*)::bigint AS \"featureCount\", ");
                sql.Append(CultureInfo.InvariantCulture,
                    $"ST_AsGeoJSON(ST_Transform(ST_Union({bufferedGeometry}), 4326)) AS \"bufferGeometry\"");
            }
            else
            {
                sql.Append(CultureInfo.InvariantCulture,
                    $"{DatabaseSchema.ObjectIdColumn} AS \"objectId\", ");
                sql.Append(CultureInfo.InvariantCulture,
                    $"ST_AsGeoJSON(ST_Transform({bufferedGeometry}, 4326)) AS \"bufferGeometry\"");
            }

            AppendClusterStatisticsColumns(sql, bufferQuery.OutStatistics);

            sql.Append(" FROM src");

            if (bufferQuery.Dissolve && hasGroupBy)
            {
                sql.Append(" GROUP BY ");
                AppendGroupByFieldExpressions(sql, groupByFields!.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Builds a density (heatmap) query that bins the filtered subset into a
    /// hex or square grid in Web Mercator and returns one row per occupied cell.
    /// </summary>
    /// <remarks>
    /// Uses PostGIS <c>ST_HexagonGrid</c> / <c>ST_SquareGrid</c> against the
    /// extent of the filtered features so the grid only spans the data envelope.
    /// Each feature centroid belongs to one intersecting cell, with grid indices
    /// breaking ties on shared edges and vertices. Distinct source features keep
    /// their own contributions even when their centroids coincide.
    /// </remarks>
    public CoreParameterizedQuery BuildDensityQuery(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(query, "density");
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var cellSizeParam = $"${paramIndex++}";
            parameters.Add(densityQuery.CellSizeMeters);

            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var geographic = IsGeographicLayer(query.SpatialReferenceSrid);
            var pointGeometry = geographic
                ? $"ST_Centroid({geometryOperand})"
                : $"ST_Centroid({EnsureMeters(geometryOperand, query.SpatialReferenceSrid)})";

            string? weightExpression = null;
            if (!string.IsNullOrWhiteSpace(densityQuery.WeightField))
            {
                if (!IsValidFieldName(densityQuery.WeightField))
                {
                    throw new ArgumentException(
                        $"Invalid weight field name: {densityQuery.WeightField}", nameof(densityQuery));
                }

                var weightFieldExpr = GetFieldExpression(densityQuery.WeightField);
                weightExpression = $"({weightFieldExpr})::numeric";
            }

            // Source CTE — pre-project geometry once and apply the input cap.
            // Geographic layers keep the degree centroid in `filtered` so the
            // azimuthal centre is that extent, then project into `src`.
            sql.Append(geographic ? "WITH filtered AS (SELECT " : "WITH src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn} AS input_id, ");
            sql.Append(geographic ? pointGeometry + " AS pt_geo" : pointGeometry + " AS pt");
            if (weightExpression != null)
            {
                sql.Append(CultureInfo.InvariantCulture, $", {weightExpression} AS weight");
            }
            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            var maxInputParam = $"${paramIndex++}";
            parameters.Add(densityQuery.MaxInputFeatures + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam}),");

            string cellGeometrySql;
            if (geographic)
            {
                var forward = BuildAeqdPipelineSql(query, query.SpatialReferenceSrid!.Value, "filtered", "pt_geo", inverse: false);
                var inverse = BuildAeqdPipelineSql(query, query.SpatialReferenceSrid!.Value, "filtered", "pt_geo", inverse: true);
                sql.Append(" src AS (SELECT input_id, ST_TransformPipeline(pt_geo, ");
                sql.Append(forward);
                sql.Append(") AS pt");
                if (weightExpression != null)
                {
                    sql.Append(", weight");
                }

                sql.Append(" FROM filtered),");
                // The azimuthal plane has no EPSG code. Stamp SRID 0 on the extent
                // so the grid and the projected points share one planar space.
                sql.Append(" bounds AS (SELECT ST_SetSRID(ST_Extent(pt)::geometry, 0) AS extent FROM src),");
                cellGeometrySql = $"ST_TransformPipeline(c.cell, {inverse})";
            }
            else
            {
                // ST_Extent returns BOX2D (SRID-less); stamp 3857 so the grid matches
                // the projected points.
                sql.Append(" bounds AS (SELECT ST_SetSRID(ST_Extent(pt)::geometry, 3857) AS extent FROM src),");
                cellGeometrySql = "ST_Transform(c.cell, 4326)";
            }

            var gridFunction = densityQuery.Mode == DensityBinningMode.HexGrid
                ? "ST_HexagonGrid"
                : "ST_SquareGrid";

            // Cells CTE — generate the grid only over the data envelope.
            sql.Append(CultureInfo.InvariantCulture,
                $" cells AS (SELECT (g).geom AS cell, (g).i, (g).j FROM bounds, LATERAL {gridFunction}({cellSizeParam}, bounds.extent::geometry) g),");

            // Intersections include both sides of an edge (and every cell at a
            // vertex). Assign each input row once, using stable grid coordinates
            // rather than deduplicating centroids or their weights.
            sql.Append(" assigned AS (SELECT DISTINCT ON (s.input_id) s.input_id, c.cell");
            if (weightExpression != null)
            {
                sql.Append(", s.weight");
            }
            sql.Append(" FROM cells c JOIN src s ON ST_Intersects(c.cell, s.pt)");
            sql.Append(" ORDER BY s.input_id, c.i, c.j)");

            // Outer query — count or weighted sum per cell, emit GeoJSON in WGS 84.
            // _inputCount carries the actual src CTE row count (capped at
            // MaxInputFeatures+1) so the handler can report InputTruncated even
            // though the cell-level count(*) only reflects features inside that
            // particular cell.
            sql.Append(" SELECT (SELECT COUNT(*)::bigint FROM src) AS \"_inputCount\"");
            sql.Append(", row_number() OVER (ORDER BY count(*) DESC)::bigint AS \"cellId\"");
            sql.Append(", count(*)::bigint AS \"featureCount\"");
            if (weightExpression != null)
            {
                sql.Append(", COALESCE(SUM(c.weight), 0)::double precision AS \"weight\"");
            }
            sql.Append(CultureInfo.InvariantCulture, $", ST_AsGeoJSON({cellGeometrySql}) AS \"cellGeometry\"");
            sql.Append(" FROM assigned c");
            sql.Append(" GROUP BY c.cell");

            // ORDER BY count desc, then LIMIT max+1 so the handler detects overflow.
            sql.Append(" ORDER BY \"featureCount\" DESC");
            var maxCellsParam = $"${paramIndex}";
            parameters.Add(densityQuery.MaxCells + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxCellsParam}");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Ensures a geometry expression is in a meters CRS (Web Mercator / 3857)
    /// for distance-based PostGIS functions like <c>ST_ClusterDBSCAN</c> and
    /// <c>ST_Buffer</c>. When the layer SRID is null (unknown) the geometry is
    /// assumed to already be in WGS 84 and is transformed; when it is already
    /// 3857 the call is a no-op.
    /// </summary>
    private static string EnsureMeters(string geometryOperand, int? sourceSrid)
    {
        if (sourceSrid.HasValue && sourceSrid.Value == 3857)
        {
            return geometryOperand;
        }

        return $"ST_Transform({geometryOperand}, 3857)";
    }

    private static bool IsGeographicLayer(int? srid)
        => srid is int value && GeographicSridClassifier.IsGeographicSrid(value);

    private static void AppendClusterSourceColumns(
        StringBuilder sql,
        string attributesSource,
        string geometryOperand,
        bool trailingComma)
    {
        sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
        sql.Append(attributesSource == DatabaseSchema.AttributesColumn
            ? $"{DatabaseSchema.AttributesColumn}, "
            : $"{attributesSource} AS {DatabaseSchema.AttributesColumn}, ");
        sql.Append(CultureInfo.InvariantCulture, $"{geometryOperand} AS geom");
        if (trailingComma)
        {
            sql.Append(", ");
        }
    }

    private void AppendClusterSourceFrom(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        ref int paramIndex,
        List<object> parameters,
        string maxInputParam)
    {
        sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
        sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
        sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");
        AppendWhereClause(sql, query, ref paramIndex, parameters);
        AppendTemporalFilter(sql, query, ref paramIndex, parameters);
        AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
        sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam})");
    }

    /// <summary>
    /// SQL expression for an azimuthal-equidistant pipeline. A request envelope
    /// supplies a constant centre. Otherwise the centre is the centroid of
    /// <paramref name="extentRelation"/>.<paramref name="geometryColumn"/>.
    /// </summary>
    private static string BuildAeqdPipelineSql(
        FeatureQuery query,
        int sourceSrid,
        string extentRelation,
        string geometryColumn,
        bool inverse)
    {
        if (TryResolveAeqdCenter(query, sourceSrid, out var latitude, out var longitude))
        {
            var lat = latitude.ToString("G17", CultureInfo.InvariantCulture);
            var lon = longitude.ToString("G17", CultureInfo.InvariantCulture);
            var pipeline = inverse
                ? $"+proj=pipeline +step +inv +proj=aeqd +lat_0={lat} +lon_0={lon} +ellps=WGS84 +units=m +step +proj=unitconvert +xy_in=rad +xy_out=deg"
                : $"+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=aeqd +lat_0={lat} +lon_0={lon} +ellps=WGS84 +units=m";
            return $"'{pipeline}'";
        }

        var latitudeSql =
            $"(SELECT to_char(ST_Y(ST_Centroid(ST_Extent({geometryColumn}))), 'FM999990.999999999') FROM {extentRelation})";
        var longitudeSql =
            $"(SELECT to_char(ST_X(ST_Centroid(ST_Extent({geometryColumn}))), 'FM999990.999999999') FROM {extentRelation})";
        return inverse
            ? $"('+proj=pipeline +step +inv +proj=aeqd +lat_0=' || {latitudeSql} || ' +lon_0=' || {longitudeSql} || ' +ellps=WGS84 +units=m +step +proj=unitconvert +xy_in=rad +xy_out=deg')"
            : $"('+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=aeqd +lat_0=' || {latitudeSql} || ' +lon_0=' || {longitudeSql} || ' +ellps=WGS84 +units=m')";
    }

    private static bool TryResolveAeqdCenter(FeatureQuery query, int sourceSrid, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (query.SpatialFilter is not { IsSimpleEnvelope: true } filter
            || filter.EnvelopeMinX is not double minX
            || filter.EnvelopeMaxX is not double maxX
            || filter.EnvelopeMinY is not double minY
            || filter.EnvelopeMaxY is not double maxY)
        {
            return false;
        }

        if (filter.Srid is int filterSrid
            && filterSrid != sourceSrid
            && !GeographicSridClassifier.IsGeographicSrid(filterSrid))
        {
            return false;
        }

        latitude = (minY + maxY) / 2d;
        var span = maxX - minX;
        longitude = span > 180d
            ? NormalizeLongitude((minX + maxX) / 2d + 180d)
            : (minX + maxX) / 2d;
        return true;
    }

    private static double NormalizeLongitude(double longitude)
    {
        var wrapped = longitude % 360d;
        if (wrapped > 180d)
        {
            wrapped -= 360d;
        }
        else if (wrapped < -180d)
        {
            wrapped += 360d;
        }

        return wrapped;
    }

    /// <summary>
    /// Appends optional aggregate-statistic columns shared by clustering and
    /// buffer-aggregate. Validates field names and reuses
    /// <see cref="BuildAggregateExpression"/> / <see cref="GetFieldExpression"/>
    /// for parity with <c>queryStatistics</c> / <c>queryH3</c>.
    /// </summary>
    private static void AppendClusterStatisticsColumns(
        StringBuilder sql, ImmutableArray<StatisticDefinition>? outStatistics)
    {
        if (!outStatistics.HasValue || outStatistics.Value.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var stat in outStatistics.Value)
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
            var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr, stat.FieldType);
            sql.Append(CultureInfo.InvariantCulture,
                $", {aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
        }
    }

    /// <summary>
    /// Emits the SELECT-list portion for group-by columns, validating each name.
    /// </summary>
    private static void AppendGroupByFieldSelectList(StringBuilder sql, ImmutableArray<string> groupByFields)
    {
        for (var i = 0; i < groupByFields.Length; i++)
        {
            if (!IsValidFieldName(groupByFields[i]))
            {
                throw new ArgumentException($"Invalid group-by field name: {groupByFields[i]}");
            }

            if (i > 0)
            {
                sql.Append(", ");
            }

            var expr = GetFieldExpression(groupByFields[i]);
            sql.Append(CultureInfo.InvariantCulture, $"{expr} AS {SanitizeAlias(groupByFields[i])}");
        }
    }

    /// <summary>
    /// Emits the GROUP BY clause column list (no aliases) for the same fields.
    /// </summary>
    private static void AppendGroupByFieldExpressions(StringBuilder sql, ImmutableArray<string> groupByFields)
    {
        for (var i = 0; i < groupByFields.Length; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(GetFieldExpression(groupByFields[i]));
        }
    }
}
