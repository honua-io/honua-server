// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Db.MySql;
using Honua.Db.MySql.Features.Infrastructure;

namespace Honua.Db.MySql.Features.FeatureStore.Services;

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

        var projectedAttributes = ResolveProjectedAttributeColumns(mapping, query);
        var columnsExpr = string.Join(", ", projectedAttributes.Select(MySqlIdentifier.Quote));

        if (query.Distinct)
        {
            // Distinct values are attribute tuples. Literal 0 keeps the reader contract
            // (GetInt64 on column 0) without uniquifying every row via the primary key.
            sb.Append(CultureInfo.InvariantCulture,
                $"SELECT DISTINCT 0 AS {mapping.QuotedPrimaryKeyColumn}, NULL AS geometry");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"SELECT {mapping.QuotedPrimaryKeyColumn}, {MySqlSpatialSql.AsWkb(mapping.QuotedGeometryColumn, _engineFlavor)} AS geometry");
        }

        if (!string.IsNullOrEmpty(columnsExpr))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {columnsExpr}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" FROM {mapping.QualifiedTableSql} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        if (query.Distinct)
        {
            if (query.Limit.HasValue || query.Offset is > 0)
            {
                AppendDistinctOrderBy(sb, query, projectedAttributes);
            }
        }
        else
        {
            AppendOrderByClause(sb, mapping, query);
        }

        AppendPagination(sb, query, ref paramIndex, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

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

        if (!query.Distinct)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"SELECT COUNT(*) FROM {mapping.QualifiedTableSql} WHERE 1=1");

            AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
            AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);

            return new ParameterizedQuery(sb.ToString(), parameters);
        }

        var projectedAttributes = ResolveProjectedAttributeColumns(mapping, query);
        var distinctColumns = projectedAttributes.Count == 0
            ? "1"
            : string.Join(", ", projectedAttributes.Select(MySqlIdentifier.Quote));
        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM (SELECT DISTINCT {distinctColumns} FROM {mapping.QualifiedTableSql} WHERE 1=1");

        AppendWhereClause(sb, mapping, query, ref paramIndex, parameters);
        AppendSpatialFilter(sb, mapping, query, ref paramIndex, parameters);
        sb.Append(") AS distinct_values");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

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

        var extentSelect = BuildExtentSelectClause(mapping);

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

    private bool UseGeographicAxes(MySqlLayerMapping mapping)
        => _engineFlavor == MySqlEngineFlavor.Mysql
            && GeographicSridClassifier.IsGeographicSrid(mapping.Srid);

    private string FormatExtentAxisX(MySqlLayerMapping mapping, string pointExpression)
        => UseGeographicAxes(mapping)
            ? $"ST_Longitude({pointExpression})"
            : $"ST_X({pointExpression})";

    private string FormatExtentAxisY(MySqlLayerMapping mapping, string pointExpression)
        => UseGeographicAxes(mapping)
            ? $"ST_Latitude({pointExpression})"
            : $"ST_Y({pointExpression})";

    private string BuildExtentEnvelopeCorner(MySqlLayerMapping mapping, int cornerIndex)
    {
        var geometryColumn = mapping.QuotedGeometryColumn;
        var indexLiteral = cornerIndex.ToString(CultureInfo.InvariantCulture);

        // MariaDB only supports 1-arg ST_SRID(geom) and computes ST_Envelope directly in
        // stored X/Y order. MySQL 8.0+ rejects ST_Envelope on geographic SRSes, so drop to
        // SRID 0 for the bounding box and re-tag the corner back to the geographic SRID
        // before passing it to ST_Longitude/ST_Latitude (which reject SRID 0).
        if (_engineFlavor == MySqlEngineFlavor.MariaDb)
        {
            return $"ST_PointN(ST_ExteriorRing(ST_Envelope({geometryColumn})), {indexLiteral})";
        }

        var cartesianCorner = $"ST_PointN(ST_ExteriorRing(ST_Envelope(ST_SRID({geometryColumn}, 0))), {indexLiteral})";
        return UseGeographicAxes(mapping)
            ? $"ST_SRID({cartesianCorner}, {mapping.Srid.ToString(CultureInfo.InvariantCulture)})"
            : cartesianCorner;
    }

    private string BuildExtentSelectClause(MySqlLayerMapping mapping)
    {
        // MySQL ST_X/ST_Y are point-only and ST_Envelope of a degenerate LineString returns a
        // LineString, breaking ST_ExteriorRing. Geographic SRSes are latitude-first on MySQL 8.0.12+,
        // so ST_Longitude/ST_Latitude keep extent corners aligned with axis-normalized WKB reads.
        var geometryColumn = mapping.QuotedGeometryColumn;
        if (mapping.GeometryType == GeometryType.Point)
        {
            return $"""
                MIN({FormatExtentAxisX(mapping, geometryColumn)}),
                MIN({FormatExtentAxisY(mapping, geometryColumn)}),
                MAX({FormatExtentAxisX(mapping, geometryColumn)}),
                MAX({FormatExtentAxisY(mapping, geometryColumn)})
                """;
        }

        if (mapping.GeometryType is GeometryType.Polygon or GeometryType.MultiPolygon)
        {
            var envelopeCorner1 = BuildExtentEnvelopeCorner(mapping, 1);
            var envelopeCorner3 = BuildExtentEnvelopeCorner(mapping, 3);
            return $"""
                MIN({FormatExtentAxisX(mapping, envelopeCorner1)}),
                MIN({FormatExtentAxisY(mapping, envelopeCorner1)}),
                MAX({FormatExtentAxisX(mapping, envelopeCorner3)}),
                MAX({FormatExtentAxisY(mapping, envelopeCorner3)})
                """;
        }

        throw MySqlUnsupportedFeature.Create(
            $"Extent queries on {mapping.GeometryType} layers are not supported by the MySQL/MariaDB " +
            "provider in this slice. Supported geometry types: Point, Polygon, MultiPolygon. " +
            "Compute extents in the application layer or use a PostGIS-backed layer.");
    }

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

    public ParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
    {
        // No window-function fast-path optimization is implemented for this slice;
        // route to the standard select query so callers do not need to special-case.
        return BuildSelectQuery(layerId, query, geometryStorageType);
    }

    private static List<string> ResolveProjectedAttributeColumns(MySqlLayerMapping mapping, FeatureQuery query)
    {
        if (query.ExcludeAttributes)
        {
            return [];
        }

        // Null/default OutFields projects every column. An explicit empty array projects
        // none. IsEmpty throws on a default ImmutableArray, so test IsDefault first.
        if (!query.OutFields.HasValue || query.OutFields.Value.IsDefault)
        {
            return mapping.AttributeColumns.ToList();
        }

        if (query.OutFields.Value.IsEmpty)
        {
            return [];
        }

        var requested = new HashSet<string>(query.OutFields.Value, StringComparer.OrdinalIgnoreCase);
        return mapping.AttributeColumns.Where(c => requested.Contains(c)).ToList();
    }

    private static void EnsureSridCompatibility(MySqlLayerMapping mapping, FeatureQuery query)
    {
        if (query.OutputSrid.HasValue && query.OutputSrid.Value != mapping.Srid)
        {
            throw MySqlUnsupportedFeature.Create(
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
            throw MySqlUnsupportedFeature.Create(
                "Temporal filters are not supported by the MySQL/MariaDB provider in this slice. " +
                "Apply temporal filtering in the calling layer or use a PostGIS-backed layer.");
        }
    }
}
