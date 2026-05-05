// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Features.FeatureStore.Services;

/// <summary>
/// Builds parameterized SQL for the MySQL/MariaDB read-only feature provider.
/// Targets MySQL 8.0.11+ and MariaDB 10.6+; spatial functions are limited to
/// the operations both engines share (no <c>ST_Transform</c>, no native MVT, no KNN).
/// </summary>
internal sealed partial class MySqlFeatureQueryBuilder : IFeatureQueryBuilder
{
    private readonly MySqlLayerMappingRegistry _layerRegistry;
    private readonly MySqlEngineFlavor _engineFlavor;

    public MySqlFeatureQueryBuilder(
        MySqlLayerMappingRegistry layerRegistry,
        MySqlEngineFlavor engineFlavor = MySqlEngineFlavor.Mysql)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
        _engineFlavor = engineFlavor;
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        EnsureSridCompatibility(mapping, query);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 0;

        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedPrimaryKeyColumn}, {MySqlSpatialSql.AsWkb(mapping.QuotedGeometryColumn, _engineFlavor)} AS geometry");

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QualifiedTableSql} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildCountQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        EnsureSridCompatibility(mapping, query);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 0;

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM {mapping.QualifiedTableSql} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildExtentQuery(
        int layerId,
        FeatureQuery? query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var effectiveQuery = query ?? new FeatureQuery();
        EnsureSridCompatibility(mapping, effectiveQuery);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 0;

        // Extent SQL must work on both MySQL 8.0.11+ and MariaDB 10.6+. Two engine
        // divergences shape the strategy:
        //   * MariaDB only supports the 1-arg ST_SRID(geom). The 2-arg setter is MySQL-only,
        //     so we cannot retag geometries to Cartesian inline.
        //   * MySQL ST_X/ST_Y are documented as point-only; using them on MultiPoint or
        //     LineString errors out. ST_Envelope of a degenerate (vertical/horizontal)
        //     LineString returns a LineString, not a Polygon, breaking ST_ExteriorRing.
        //
        // Supported per-row patterns:
        //   * Point: MIN/MAX of ST_X/ST_Y on the column directly.
        //   * Polygon/MultiPolygon: ST_Envelope is always a Polygon (polygons have area),
        //     so ST_PointN(ST_ExteriorRing(ST_Envelope(geom)), 1|3) is safe.
        //
        // Other types (MultiPoint, LineString, MultiLineString, GeometryCollection, None)
        // are rejected explicitly — callers should compute extents in the application
        // layer or use a PostGIS-backed layer.
        //
        // Axis order: ST_X/ST_Y return the first/second stored axis. For MySQL 8 with a
        // geographic SRS (e.g. 4326) that is latitude-first; for MariaDB it is the WKB
        // order. The slice does not normalize this; the documented contract is "as stored".
        var extentSelect = mapping.GeometryType switch
        {
            GeometryType.Point => $"""
                MIN(ST_X({mapping.QuotedGeometryColumn})),
                MIN(ST_Y({mapping.QuotedGeometryColumn})),
                MAX(ST_X({mapping.QuotedGeometryColumn})),
                MAX(ST_Y({mapping.QuotedGeometryColumn}))
                """,
            GeometryType.Polygon or GeometryType.MultiPolygon => $"""
                MIN(ST_X(ST_PointN(ST_ExteriorRing(ST_Envelope({mapping.QuotedGeometryColumn})), 1))),
                MIN(ST_Y(ST_PointN(ST_ExteriorRing(ST_Envelope({mapping.QuotedGeometryColumn})), 1))),
                MAX(ST_X(ST_PointN(ST_ExteriorRing(ST_Envelope({mapping.QuotedGeometryColumn})), 3))),
                MAX(ST_Y(ST_PointN(ST_ExteriorRing(ST_Envelope({mapping.QuotedGeometryColumn})), 3)))
                """,
            _ => throw new NotSupportedException(
                $"Extent queries on {mapping.GeometryType} layers are not supported by the MySQL/MariaDB " +
                "provider in this slice. Supported geometry types: Point, Polygon, MultiPolygon. " +
                "Compute extents in the application layer or use a PostGIS-backed layer.")
        };

        sb.Append(CultureInfo.InvariantCulture,
            $"""
            SELECT
                {extentSelect}
            FROM {mapping.QualifiedTableSql}
            WHERE {mapping.QuotedGeometryColumn} IS NOT NULL
            """);

        AppendWhereClause(sb, mapping, effectiveQuery, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, effectiveQuery, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        EnsureSridCompatibility(mapping, query);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 0;

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedPrimaryKeyColumn} FROM {mapping.QualifiedTableSql} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        AppendOrderByClause(sb, mapping, query);
        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        // No window-function fast-path optimization is implemented for this slice;
        // route to the standard select query so callers do not need to special-case.
        return BuildSelectQuery(layerId, query, geometryStorageType);
    }

    private static string BuildAttributeColumnsExpression(MySqlLayerMapping mapping, FeatureQuery query)
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

        return string.Join(", ", columns.Select(MySqlIdentifier.Quote));
    }

    private static void EnsureSridCompatibility(MySqlLayerMapping mapping, FeatureQuery query)
    {
        if (query.OutputSrid.HasValue && query.OutputSrid.Value != mapping.Srid)
        {
            throw new NotSupportedException(
                $"Output SRID transforms are not supported by the MySQL/MariaDB provider " +
                $"(layer SRID is {mapping.Srid}, requested {query.OutputSrid.Value}). " +
                $"Pre-project geometries to the layer SRID or use a PostGIS-backed layer.");
        }

        // Temporal filters arrive on FeatureQuery from OGC API Features (datetime),
        // STAC search, and OData time-window queries. The slice does not translate
        // them to MySQL SQL — surface NotSupportedException eagerly so callers
        // cannot silently lose the constraint and return unfiltered rows.
        if (query.TemporalFilter.HasValue)
        {
            throw new NotSupportedException(
                "Temporal filters are not supported by the MySQL/MariaDB provider in this slice. " +
                "Apply temporal filtering in the calling layer or use a PostGIS-backed layer.");
        }
    }
}
