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

    public MySqlFeatureQueryBuilder(MySqlLayerMappingRegistry layerRegistry)
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
        EnsureSridCompatibility(mapping, query);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var paramIndex = 0;

        var columnsExpr = BuildAttributeColumnsExpression(mapping, query);

        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT {mapping.QuotedPrimaryKeyColumn}, ST_AsWKB({mapping.QuotedGeometryColumn}) AS geometry");

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

        // MySQL 8.0+ / MariaDB 10.6+ have two extent gotchas: ST_Envelope is rejected on
        // geographic SRSes, and ST_Envelope of a degenerate geometry (point or vertical/
        // horizontal line) is not a 5-point polygon, so ST_ExteriorRing/PointN return NULL.
        //
        // Strategy:
        //   * Points/MultiPoints → MIN/MAX of ST_X/ST_Y per row (works on geographic SRS,
        //     no envelope needed).
        //   * Other geometries → first ST_SRID(geom, 0) to retag as Cartesian (preserves
        //     coordinates), then extract the envelope corners via ST_PointN(ST_ExteriorRing).
        // Use ST_SRID(..., 0) to retag as Cartesian; ST_X/ST_Y on a geographic point
        // observes axis order (lat-lon for EPSG:4326), which yields swapped coordinates
        // versus the as-stored representation. Cartesian retagging keeps coordinates
        // consistent with what callers wrote.
        var cartesianGeom = $"ST_SRID({mapping.QuotedGeometryColumn}, 0)";

        var extentSelect = mapping.GeometryType is GeometryType.Point or GeometryType.MultiPoint
            ? $"""
                MIN(ST_X({cartesianGeom})),
                MIN(ST_Y({cartesianGeom})),
                MAX(ST_X({cartesianGeom})),
                MAX(ST_Y({cartesianGeom}))
                """
            : $"""
                MIN(ST_X(ST_PointN(ST_ExteriorRing(ST_Envelope({cartesianGeom})), 1))),
                MIN(ST_Y(ST_PointN(ST_ExteriorRing(ST_Envelope({cartesianGeom})), 1))),
                MAX(ST_X(ST_PointN(ST_ExteriorRing(ST_Envelope({cartesianGeom})), 3))),
                MAX(ST_Y(ST_PointN(ST_ExteriorRing(ST_Envelope({cartesianGeom})), 3)))
                """;

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
