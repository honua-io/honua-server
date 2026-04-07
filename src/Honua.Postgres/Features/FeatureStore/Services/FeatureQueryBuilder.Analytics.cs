// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

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
    /// When the layer SRID is geographic the geometry is transformed to Web
    /// Mercator (3857) inside the CTE so DBSCAN <c>eps</c> is interpreted in
    /// meters. The transform is paid once per row regardless of the number of
    /// statistics requested.
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
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var metersGeometry = EnsureMeters(geometryOperand, query.SpatialReferenceSrid);

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

            sql.Append("WITH src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.AttributesColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.GeometryColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{clusterExpression} AS cluster_id");
            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            // LIMIT n+1 so the caller can detect overflow without scanning further.
            var maxInputParam = $"${paramIndex++}";
            parameters.Add(clusterQuery.MaxInputFeatures + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam})");

            if (clusterQuery.ReturnHullPerCluster)
            {
                // Aggregated mode — one row per cluster with the convex hull and stats.
                sql.Append(" SELECT cluster_id::bigint AS \"clusterId\"");
                sql.Append(", COUNT(*)::bigint AS \"featureCount\"");
                sql.Append(CultureInfo.InvariantCulture,
                    $", ST_AsGeoJSON(ST_ConvexHull(ST_Collect({DatabaseSchema.GeometryColumn}))) AS \"clusterGeometry\"");
                AppendClusterStatisticsColumns(sql, clusterQuery.OutStatistics);
                sql.Append(" FROM src WHERE cluster_id IS NOT NULL");
                sql.Append(" GROUP BY cluster_id ORDER BY \"featureCount\" DESC");
            }
            else
            {
                // Per-feature mode — one row per source feature carrying its assigned cluster id.
                sql.Append(CultureInfo.InvariantCulture,
                    $" SELECT {DatabaseSchema.ObjectIdColumn} AS \"objectId\"");
                sql.Append(", cluster_id::bigint AS \"clusterId\"");
                sql.Append(CultureInfo.InvariantCulture,
                    $", {DatabaseSchema.AttributesColumn} AS \"attributes\"");
                sql.Append(CultureInfo.InvariantCulture,
                    $", ST_AsGeoJSON({DatabaseSchema.GeometryColumn}) AS \"geometry\"");
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
    /// Buffers each input feature by <see cref="BufferAggregateQuery.Distance"/>
    /// (converted to meters), then either <c>ST_Union</c>s the buffered geometries
    /// per group (when <see cref="BufferAggregateQuery.Dissolve"/> is true) or
    /// returns the per-row buffers. The buffer is computed in Web Mercator and
    /// the resulting geometry is emitted as GeoJSON in WGS 84 to match the rest
    /// of the analytics surface.
    /// </remarks>
    public CoreParameterizedQuery BuildBufferAggregateQuery(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
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
            var bufferedGeometry = $"ST_Buffer({metersGeometry}, {distanceParam})";

            sql.Append("SELECT ");

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

            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            if (bufferQuery.Dissolve && hasGroupBy)
            {
                sql.Append(" GROUP BY ");
                AppendGroupByFieldExpressions(sql, groupByFields!.Value);
            }
            else if (bufferQuery.Dissolve)
            {
                // Single dissolved row across the entire filtered set — no GROUP BY needed.
            }

            // LIMIT n+1 so the handler can detect overflow before materializing more rows.
            var maxInputParam = $"${paramIndex++}";
            parameters.Add(bufferQuery.MaxInputFeatures + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam}");

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
    /// The cell-side join uses <c>ST_Intersects</c> on the centroid for points
    /// (the common density use case) which keeps the join cardinality at exactly
    /// one row per source feature regardless of geometry type.
    /// </remarks>
    public CoreParameterizedQuery BuildDensityQuery(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            var cellSizeParam = $"${paramIndex++}";
            parameters.Add(densityQuery.CellSizeMeters);

            var geometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, query.SpatialReferenceSrid);
            var metersGeometry = EnsureMeters(geometryOperand, query.SpatialReferenceSrid);
            var pointGeometry = $"ST_Centroid({metersGeometry})";

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
            sql.Append("WITH src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{pointGeometry} AS pt");
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

            // Bounding box CTE drives ST_HexagonGrid / ST_SquareGrid extent.
            // ST_Extent returns BOX2D (SRID-less); cast to geometry and explicitly
            // stamp SRID 3857 so the generated grid cells match the point SRID in
            // the outer ST_Intersects join (EnsureMeters always projects to 3857).
            sql.Append(" bounds AS (SELECT ST_SetSRID(ST_Extent(pt)::geometry, 3857) AS extent FROM src),");

            var gridFunction = densityQuery.Mode == DensityBinningMode.HexGrid
                ? "ST_HexagonGrid"
                : "ST_SquareGrid";

            // Cells CTE — generate the grid only over the data envelope.
            sql.Append(CultureInfo.InvariantCulture,
                $" cells AS (SELECT (g).geom AS cell FROM bounds, LATERAL {gridFunction}({cellSizeParam}, bounds.extent::geometry) g)");

            // Outer query — count or weighted sum per cell, emit GeoJSON in WGS 84.
            sql.Append(" SELECT row_number() OVER (ORDER BY count(*) DESC)::bigint AS \"cellId\"");
            sql.Append(", count(*)::bigint AS \"featureCount\"");
            if (weightExpression != null)
            {
                sql.Append(", COALESCE(SUM(s.weight), 0)::double precision AS \"weight\"");
            }
            sql.Append(", ST_AsGeoJSON(ST_Transform(c.cell, 4326)) AS \"cellGeometry\"");
            sql.Append(" FROM cells c JOIN src s ON ST_Intersects(c.cell, s.pt)");
            sql.Append(" GROUP BY c.cell");

            // ORDER BY count desc, then LIMIT max+1 so the handler detects overflow.
            sql.Append(" ORDER BY \"featureCount\" DESC");
            var maxCellsParam = $"${paramIndex++}";
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
            var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr);
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
