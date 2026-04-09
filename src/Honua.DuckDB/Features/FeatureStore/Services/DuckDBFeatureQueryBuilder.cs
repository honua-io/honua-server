// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Tiles;
using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Features.FeatureStore.Services;

/// <summary>
/// Builds SQL queries for DuckDB feature store operations.
/// DuckDB uses direct table columns (not JSONB) and PostGIS-compatible spatial functions.
/// </summary>
internal sealed partial class DuckDBFeatureQueryBuilder : IFeatureQueryBuilder
{
    private readonly DuckDBLayerRegistry _layerRegistry;

    public DuckDBFeatureQueryBuilder(DuckDBLayerRegistry layerRegistry)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var geometryExpr = BuildGeometryWkbExpression(mapping, query);
        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedObjectIdColumn}, {geometryExpr}");

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildProjectedPointQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var geomExpr = BuildTransformedGeometryExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT ST_X({geomExpr}) AS x, ST_Y({geomExpr}) AS y FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedObjectIdColumn} FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectFlatGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException("DuckDB provider does not support native FlatGeobuf encoding.");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException("DuckDB provider does not support native Geobuf encoding.");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        // DuckDB spatial does not have ST_AsGML; return WKB and let server-side NTS convert
        return BuildEncodedGeometrySelectQuery(layerId, query, "ST_AsText");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        return BuildEncodedGeometrySelectQuery(layerId, query, "ST_AsGeoJSON");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        // DuckDB spatial does not have ST_AsKML; return WKT and let server-side NTS convert
        return BuildEncodedGeometrySelectQuery(layerId, query, "ST_AsText");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildCountQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var geometryExpr = BuildGeometryWkbExpression(mapping, query);
        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedObjectIdColumn}, {geometryExpr}");

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $", COUNT(*) OVER() AS {DuckDBQueryEncoding.InternalTotalCountColumn}");

        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        return BuildOptimizedEncodedSelectQuery(layerId, query, "ST_AsText");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        return BuildOptimizedEncodedSelectQuery(layerId, query, "ST_AsGeoJSON");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        return BuildOptimizedEncodedSelectQuery(layerId, query, "ST_AsText");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildExtentQuery(
        int layerId,
        FeatureQuery? query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var effectiveQuery = query ?? new FeatureQuery();
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var geomExpr = BuildTransformedGeometryExpression(mapping, effectiveQuery);

        sb.Append(CultureInfo.InvariantCulture,
            $"""
            SELECT MIN(ST_XMin({geomExpr})),
                   MIN(ST_YMin({geomExpr})),
                   MAX(ST_XMax({geomExpr})),
                   MAX(ST_YMax({geomExpr}))
            FROM {mapping.QuotedTableName}
            WHERE {mapping.QuotedGeometryColumn} IS NOT NULL
            """);

        AppendWhereClause(sb, mapping, effectiveQuery, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, effectiveQuery, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildTemporalExtentQuery(
        int layerId,
        string fieldName,
        FieldType fieldType)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        ValidateFieldName(fieldName);

        var fieldExpression = $"\"{fieldName}\"";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"""
            SELECT MIN({fieldExpression}) AS min_value, MAX({fieldExpression}) AS max_value
            FROM {mapping.QuotedTableName}
            WHERE {fieldExpression} IS NOT NULL
            """);

        return new ParameterizedQuery(sb.ToString(), []);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildStatisticsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        if (!query.OutStatistics.HasValue || query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            return new ParameterizedQuery($"SELECT 1 WHERE FALSE", []);
        }

        var statisticColumnsExpr = BuildStatisticColumns(query.OutStatistics);

        var groupByColumns = new List<string>();
        if (query.GroupByFields.HasValue && !query.GroupByFields.Value.IsDefaultOrEmpty)
        {
            foreach (var field in query.GroupByFields.Value)
            {
                ValidateFieldName(field);
                groupByColumns.Add($"\"{field}\"");
            }
        }

        sb.Append("SELECT ");
        if (groupByColumns.Count > 0)
        {
            sb.Append(string.Join(", ", groupByColumns));
            sb.Append(", ");
        }

        sb.Append(statisticColumnsExpr);
        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        if (groupByColumns.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" GROUP BY {string.Join(", ", groupByColumns)}");
        }

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildTopFeaturesQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        if (query.TopFilter is not { } topFilter)
        {
            return BuildSelectQuery(layerId, query, geometryStorageType);
        }

        foreach (var field in topFilter.GroupByFields)
        {
            ValidateFieldName(field);
        }

        foreach (var orderBy in topFilter.OrderByFields)
        {
            ValidateFieldName(orderBy.Field);
        }

        var geometryExpr = BuildGeometryWkbExpression(mapping, query);
        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);
        var partitionFields = string.Join(", ", topFilter.GroupByFields.Select(f => $"\"{f}\""));

        var orderByExprs = new List<string>();
        foreach (var orderBy in topFilter.OrderByFields)
        {
            var dir = orderBy.Ascending ? "ASC" : "DESC";
            orderByExprs.Add($"\"{orderBy.Field}\" {dir}");
        }

        var orderByStr = string.Join(", ", orderByExprs);

        sb.Append(CultureInfo.InvariantCulture,
            $"""
            SELECT {mapping.QuotedObjectIdColumn}, {geometryExpr}
            """);
        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"""
             FROM (
                SELECT *, ROW_NUMBER() OVER(PARTITION BY {partitionFields} ORDER BY {orderByStr}) AS rn
                FROM {mapping.QuotedTableName}
                WHERE 1=1
            """);

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        sb.Append(CultureInfo.InvariantCulture,
            $") AS ranked WHERE ranked.rn <= {topFilter.TopCount}");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildDateBinsQuery(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        ValidateFieldName(dateBin.BinField);
        var fieldExpr = $"\"{dateBin.BinField}\"";

        string binExpr;
        if (dateBin.IsCalendarBin && dateBin.CalendarUnit != null)
        {
            var truncUnit = ValidateCalendarUnit(dateBin.CalendarUnit);
            binExpr = $"date_trunc('{truncUnit}', {fieldExpr})";
        }
        else if (dateBin.FixedInterval.HasValue)
        {
            var intervalSeconds = (long)dateBin.FixedInterval.Value.TotalSeconds;
            binExpr = $"date_trunc('second', {fieldExpr} - INTERVAL '{intervalSeconds} seconds' * (EXTRACT(EPOCH FROM {fieldExpr}) / {intervalSeconds} - FLOOR(EXTRACT(EPOCH FROM {fieldExpr}) / {intervalSeconds})))";
        }
        else
        {
            throw new ArgumentException("Date bin must specify either CalendarUnit or FixedInterval.");
        }

        var statisticColumns = BuildStatisticColumns(dateBin.OutStatistics);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {binExpr} AS bin_start");

        if (!string.IsNullOrEmpty(statisticColumns))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {statisticColumns}");
        }
        else
        {
            sb.Append(", COUNT(*) AS count");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $" FROM {mapping.QuotedTableName} WHERE {fieldExpr} IS NOT NULL");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        sb.Append(CultureInfo.InvariantCulture, $" GROUP BY {binExpr} ORDER BY bin_start");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        ValidateFieldName(binDefinition.Field);
        var fieldExpr = $"\"{binDefinition.Field}\"";

        if (binDefinition.Type == BinType.Date && binDefinition.DateBin is { } dateBinDef)
        {
            return BuildDateBinsQuery(layerId, query, dateBinDef, geometryStorageType);
        }

        string binExpr;
        if (binDefinition.Type == BinType.FixedInterval && binDefinition.IntervalSize.HasValue)
        {
            var start = binDefinition.IntervalStart ?? 0;
            binExpr = $"FLOOR(({fieldExpr} - {start.ToString(CultureInfo.InvariantCulture)}) / {binDefinition.IntervalSize.Value.ToString(CultureInfo.InvariantCulture)}) * {binDefinition.IntervalSize.Value.ToString(CultureInfo.InvariantCulture)} + {start.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            binExpr = fieldExpr;
        }

        var statisticColumns = BuildStatisticColumns(binDefinition.OutStatistics);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {binExpr} AS bin_value");

        if (!string.IsNullOrEmpty(statisticColumns))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {statisticColumns}");
        }
        else
        {
            sb.Append(", COUNT(*) AS count");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        sb.Append(CultureInfo.InvariantCulture, $" GROUP BY {binExpr} ORDER BY bin_value");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildMvtTileQuery(
        int layerId, int x, int y, int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException("DuckDB provider does not support native MVT tile generation (no ST_AsMVT).");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildH3AggregationQuery(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException("DuckDB provider does not support H3 hexagonal aggregation.");
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildH3TileQuery(
        int layerId, int x, int y, int z, int resolution,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException("DuckDB provider does not support H3 tile generation.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The spatial analytics endpoints (ticket #342) are PostGIS-only — they rely on
    /// <c>ST_ClusterDBSCAN</c>/<c>ST_ClusterKMeans</c>, which DuckDB Spatial does not provide.
    /// The DuckDB provider is read-only for catalog/GeoParquet workflows and intentionally
    /// surfaces this gap with the same <see cref="NotSupportedException"/> pattern used by
    /// the MVT and H3 helpers above.
    /// </remarks>
    public ParameterizedQuery BuildClusterQuery(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException(
            "DuckDB provider does not support spatial clustering (ST_ClusterDBSCAN / ST_ClusterKMeans).");
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="BuildClusterQuery"/> for the rationale behind the DuckDB gap.</remarks>
    public ParameterizedQuery BuildBufferAggregateQuery(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException(
            "DuckDB provider does not support buffer-aggregate analytics (requires PostGIS ST_Buffer/ST_Union).");
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="BuildClusterQuery"/> for the rationale behind the DuckDB gap.</remarks>
    public ParameterizedQuery BuildDensityQuery(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException(
            "DuckDB provider does not support density binning (requires PostGIS ST_HexagonGrid / ST_SquareGrid).");
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="BuildClusterQuery"/> for the rationale behind the DuckDB gap.</remarks>
    public ParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        throw new NotSupportedException(
            "DuckDB provider does not support spatial join analytics (requires PostGIS predicate pushdown).");
    }

    #region Private helpers

    private ParameterizedQuery BuildEncodedGeometrySelectQuery(
        int layerId, FeatureQuery query, string geometryFunction)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);
        var geomExpr = BuildTransformedGeometryExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedObjectIdColumn}, {geometryFunction}({geomExpr}) AS geometry");

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private ParameterizedQuery BuildOptimizedEncodedSelectQuery(
        int layerId, FeatureQuery query, string geometryFunction)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 1;

        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);
        var geomExpr = BuildTransformedGeometryExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedObjectIdColumn}, {geometryFunction}({geomExpr}) AS geometry");

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $", COUNT(*) OVER() AS {DuckDBQueryEncoding.InternalTotalCountColumn}");
        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QuotedTableName} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query, ref paramIndex, parameters);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Returns a geometry SQL expression with ST_Transform applied when the requested
    /// output SRID differs from the layer's source SRID.
    /// </summary>
    private static string BuildTransformedGeometryExpression(DuckDBLayerMapping mapping, FeatureQuery query)
    {
        if (query.OutputSrid.HasValue && query.OutputSrid.Value != mapping.Srid)
        {
            return $"ST_Transform({mapping.QuotedGeometryColumn}, 'EPSG:{mapping.Srid}', 'EPSG:{query.OutputSrid.Value}')";
        }

        return mapping.QuotedGeometryColumn;
    }

    private static string BuildGeometryWkbExpression(DuckDBLayerMapping mapping, FeatureQuery query)
    {
        var geomExpr = BuildTransformedGeometryExpression(mapping, query);
        return $"ST_AsWKB({geomExpr})";
    }

    private static string BuildAttributeColumnsExpression(DuckDBLayerMapping mapping, FeatureQuery query)
    {
        if (query.ExcludeAttributes)
        {
            return string.Empty;
        }

        IEnumerable<string> columns;
        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
        {
            var requested = new HashSet<string>(query.OutFields.Value, StringComparer.OrdinalIgnoreCase);
            columns = mapping.AttributeColumns.Where(c => requested.Contains(c));
        }
        else
        {
            columns = mapping.AttributeColumns;
        }

        return string.Join(", ", columns.Select(c => $"\"{c}\""));
    }

    private static void AppendWhereClause(
        StringBuilder sb,
        DuckDBLayerMapping mapping,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.SqlFilter != null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" AND ({ConvertNamedParametersToPositional(query.SqlFilter.Sql, ref paramIndex)})");

            foreach (var param in query.SqlFilter.Parameters)
            {
                parameters.Add(param ?? DBNull.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterizedClause = ParseAndParameterizeWhereClause(
                query.Where.Trim(), mapping, ref paramIndex, parameters);
            sb.Append(CultureInfo.InvariantCulture, $" AND ({parameterizedClause})");
        }

        if (query.ObjectIds.HasValue && !query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            var objectIds = query.ObjectIds.Value;
            var placeholders = new string[objectIds.Length];
            for (var i = 0; i < objectIds.Length; i++)
            {
                placeholders[i] = $"${paramIndex++}";
                parameters.Add(objectIds[i]);
            }

            sb.Append(CultureInfo.InvariantCulture,
                $" AND {mapping.QuotedObjectIdColumn} IN ({string.Join(", ", placeholders)})");
        }
    }

    private static void AppendSpatialFilter(
        StringBuilder sb,
        DuckDBLayerMapping mapping,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (!query.SpatialFilter.HasValue)
        {
            return;
        }

        var filter = query.SpatialFilter.Value;
        var geomCol = mapping.QuotedGeometryColumn;
        var filterGeomParam = BuildFilterGeometryExpression(filter, mapping, ref paramIndex, parameters);

        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects =>
                $"ST_Intersects({geomCol}, {filterGeomParam})",
            SpatialRelationship.Within =>
                $"ST_Within({geomCol}, {filterGeomParam})",
            SpatialRelationship.Contains =>
                $"ST_Contains({geomCol}, {filterGeomParam})",
            SpatialRelationship.EnvelopeIntersects =>
                $"ST_Intersects(ST_Envelope({geomCol}), ST_Envelope({filterGeomParam}))",
            SpatialRelationship.Crosses =>
                $"ST_Crosses({geomCol}, {filterGeomParam})",
            SpatialRelationship.Touches =>
                $"ST_Touches({geomCol}, {filterGeomParam})",
            SpatialRelationship.Overlaps =>
                $"ST_Overlaps({geomCol}, {filterGeomParam})",
            SpatialRelationship.Disjoint =>
                $"ST_Disjoint({geomCol}, {filterGeomParam})",
            SpatialRelationship.Equals =>
                $"ST_Equals({geomCol}, {filterGeomParam})",
            SpatialRelationship.WithinDistance when filter.Distance.HasValue =>
                BuildWithinDistanceClause(geomCol, filterGeomParam, filter, mapping, ref paramIndex, parameters),
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the DuckDB provider.")
        };

        sb.Append(CultureInfo.InvariantCulture, $" AND {clause}");
    }

    private static void AppendOrderByClause(
        StringBuilder sb,
        DuckDBLayerMapping mapping,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var clauses = new List<string>();
        foreach (var orderBy in query.OrderBy.Value)
        {
            ValidateFieldName(orderBy.Field);
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"\"{orderBy.Field}\" {direction}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" ORDER BY {string.Join(", ", clauses)}");
    }

    private static void AppendPagination(
        StringBuilder sb,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.Limit.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
            parameters.Add(query.Limit.Value);
        }

        if (query.Offset.HasValue && query.Offset.Value > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
            parameters.Add(query.Offset.Value);
        }
    }

    private static string BuildStatisticColumns(
        System.Collections.Immutable.ImmutableArray<StatisticDefinition>? statistics)
    {
        if (!statistics.HasValue || statistics.Value.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var columns = new List<string>();
        foreach (var stat in statistics.Value)
        {
            ValidateFieldName(stat.OnStatisticField);
            ValidateFieldName(stat.OutStatisticFieldName);
            var fieldExpr = $"\"{stat.OnStatisticField}\"";
            var alias = $"\"{stat.OutStatisticFieldName}\"";
            var statExpr = stat.StatisticType switch
            {
                StatisticType.Count => $"COUNT({fieldExpr})",
                StatisticType.Sum => $"SUM({fieldExpr})",
                StatisticType.Min => $"MIN({fieldExpr})",
                StatisticType.Max => $"MAX({fieldExpr})",
                StatisticType.Avg => $"AVG({fieldExpr})",
                StatisticType.Stddev => $"STDDEV({fieldExpr})",
                StatisticType.Var => $"VAR_SAMP({fieldExpr})",
                _ => throw new NotSupportedException($"Unsupported statistic type: {stat.StatisticType}")
            };
            columns.Add($"{statExpr} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    private static void ValidateFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || !FieldNameRegex().IsMatch(fieldName))
        {
            throw new ArgumentException($"Invalid field name: {fieldName}");
        }
    }

    private static string ValidateCalendarUnit(string unit) =>
        unit.ToLowerInvariant() switch
        {
            "year" => "year",
            "quarter" => "quarter",
            "month" => "month",
            "week" => "week",
            "day" => "day",
            "hour" => "hour",
            "minute" => "minute",
            "second" => "second",
            "millisecond" => "millisecond",
            "microsecond" => "microsecond",
            _ => throw new ArgumentException($"Unsupported calendar unit: {unit}")
        };

    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameterRegex();

    private static string ConvertNamedParametersToPositional(string sql, ref int paramIndex)
    {
        var currentIndex = paramIndex;
        var result = NamedParameterRegex().Replace(
            sql,
            _ => $"${currentIndex++}");
        paramIndex = currentIndex;
        return result;
    }

    private static string ParseAndParameterizeWhereClause(
        string whereClause,
        DuckDBLayerMapping mapping,
        ref int paramIndex,
        List<object> parameters)
    {
        // Use direct column references for DuckDB (no JSONB path expressions)
        // Delegate to shared WHERE parsing with DuckDB column mapping
        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("WHERE clause format not supported.");
        }

        var parameterizedExpressions = new List<string>(expressions.Count);

        foreach (var expression in expressions)
        {
            var trimmed = expression.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("WHERE clause format not supported.");
            }

            if (trimmed.Equals("1=1", StringComparison.Ordinal) ||
                trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                parameterizedExpressions.Add("TRUE");
                continue;
            }

            // NULL check pattern: field IS [NOT] NULL
            var nullMatch = NullCheckRegex().Match(trimmed);
            if (nullMatch.Success)
            {
                var field = nullMatch.Groups["field"].Value;
                ValidateFieldName(field);
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"\"{field}\" IS {notClause}NULL");
                continue;
            }

            // Comparison pattern: field op value
            var compMatch = ComparisonRegex().Match(trimmed);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                ValidateFieldName(field);
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var valueToken = compMatch.Groups["value"].Value;
                var value = ParseValueToken(valueToken);

                parameterizedExpressions.Add($"\"{field}\" {op} ${paramIndex++}");
                parameters.Add(value);
                continue;
            }

            throw new ArgumentException("WHERE clause format not supported.");
        }

        return string.Join(" AND ", parameterizedExpressions);
    }

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    private static string NormalizeOperator(string op) => op.Trim().ToUpperInvariant() switch
    {
        "NOT LIKE" => "NOT LIKE",
        "LIKE" => "LIKE",
        ">=" => ">=",
        "<=" => "<=",
        "!=" or "<>" => "!=",
        "=" => "=",
        ">" => ">",
        "<" => "<",
        _ => throw new ArgumentException($"Unsupported operator: {op}")
    };

    private static object ParseValueToken(string valueToken)
    {
        if (valueToken.StartsWith('\'') && valueToken.EndsWith('\''))
        {
            return valueToken[1..^1].Replace("''", "'");
        }

        if (decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return num;
        }

        return valueToken;
    }

    private static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];
            if (!inQuotes && c == '\'')
            {
                inQuotes = true;
                current.Append(c);
            }
            else if (inQuotes && c == '\'')
            {
                if (i + 1 < whereClause.Length && whereClause[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                }
                else
                {
                    inQuotes = false;
                    current.Append(c);
                }
            }
            else if (!inQuotes && i + 3 <= whereClause.Length &&
                     whereClause.Substring(i, 3).Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                     (i == 0 || !char.IsLetterOrDigit(whereClause[i - 1])) &&
                     (i + 3 >= whereClause.Length || !char.IsLetterOrDigit(whereClause[i + 3])))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
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

    /// <summary>
    /// Builds the SQL expression for a spatial filter geometry parameter, transforming
    /// it from <see cref="SpatialFilter.Srid"/> into the layer SRID when they differ
    /// so spatial predicates compare coordinates in the same CRS.
    /// </summary>
    private static string BuildFilterGeometryExpression(
        SpatialFilter filter,
        DuckDBLayerMapping mapping,
        ref int paramIndex,
        List<object> parameters)
    {
        var baseGeometry = $"ST_GeomFromWKB(${paramIndex++})";
        parameters.Add(filter.Geometry);

        if (filter.Srid.HasValue && filter.Srid.Value > 0 && filter.Srid.Value != mapping.Srid)
        {
            return $"ST_Transform({baseGeometry}, 'EPSG:{filter.Srid.Value}', 'EPSG:{mapping.Srid}')";
        }

        return baseGeometry;
    }

    private static string BuildWithinDistanceClause(
        string geomCol, string filterGeomParam,
        SpatialFilter filter, DuckDBLayerMapping mapping,
        ref int paramIndex, List<object> parameters)
    {
        var distanceInMeters = ConvertDistanceToMeters(filter.Distance!.Value, filter.DistanceUnit);
        parameters.Add(distanceInMeters);

        // DuckDB ST_DWithin operates in CRS units. For geographic CRS (degrees),
        // passing meters would be wildly incorrect. Use ST_Distance_Spheroid for
        // geodesic distance in meters on the WGS84 spheroid instead.
        if (IsGeographicSrid(mapping.Srid))
        {
            return $"ST_Distance_Spheroid({geomCol}, {filterGeomParam}) <= ${paramIndex++}";
        }

        return $"ST_DWithin({geomCol}, {filterGeomParam}, ${paramIndex++})";
    }

    /// <summary>
    /// Returns <c>true</c> for SRIDs known to be geographic (lat/lon degree-based) CRSs.
    /// Uses an explicit enumeration rather than the 4000-4999 range because that range
    /// includes non-geographic SRIDs (e.g. EPSG:4978 WGS 84 geocentric in meters) which
    /// would cause incorrect distance function selection.
    /// </summary>
    private static bool IsGeographicSrid(int srid) =>
        srid is 4326    // WGS 84
            or 4269     // NAD83
            or 4267     // NAD27
            or 4258     // ETRS89
            or 4283     // GDA94
            or 4617     // NAD83(CSRS)
            or 4759;    // NAD83(NSRS2007)

    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit) =>
        unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Feet => distance * 0.3048,
            DistanceUnit.Kilometers => distance * 1000.0,
            DistanceUnit.Miles => distance * 1609.344,
            _ => distance
        };

    #endregion
}
