// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.Infrastructure;
using Honua.Databricks.Queries.Filters;

namespace Honua.Databricks.Features.FeatureStore.Services;

/// <summary>
/// Builds Databricks SQL (Spark SQL) text for the read-only feature provider.
/// </summary>
/// <remarks>
/// <para>Geometry is selected as a WKB hex string via
/// <c>hex(st_asbinary(`geom`))</c> so the data-access layer can decode it to the
/// canonical <see cref="Feature.Geometry"/> byte array without an external geometry
/// library. The <c>ST_*</c> functions require a Databricks runtime / DBSQL with
/// spatial-function support; see the provider documentation for the limitation.</para>
/// <para>Filtering forwards the canonical <see cref="FeatureQuery.Where"/> clause
/// as-is — the GeoServices REST WHERE syntax is broadly compatible with Spark SQL
/// scalar predicates. Parameterized SQL fragments, temporal filters, and non-envelope
/// spatial filters are rejected rather than silently dropped so callers never receive
/// over-broad results.</para>
/// </remarks>
internal interface IDatabricksFeatureQueryBuilder
{
    /// <summary>Builds a paged feature SELECT returning id, WKB-hex geometry, and attributes.</summary>
    DatabricksSqlStatement BuildSelect(DatabricksLayerMapping mapping, FeatureQuery query);

    /// <summary>Builds a COUNT query honoring the same filters as the SELECT.</summary>
    DatabricksSqlStatement BuildCount(DatabricksLayerMapping mapping, FeatureQuery query);

    /// <summary>Builds an envelope-extent query over the filtered subset.</summary>
    DatabricksSqlStatement BuildExtent(DatabricksLayerMapping mapping, FeatureQuery? query);

    /// <summary>Builds a query returning only primary-key values.</summary>
    DatabricksSqlStatement BuildObjectIds(DatabricksLayerMapping mapping, FeatureQuery query);
}

/// <summary>
/// Default <see cref="IDatabricksFeatureQueryBuilder"/> implementation.
/// </summary>
internal sealed class DatabricksFeatureQueryBuilder : IDatabricksFeatureQueryBuilder
{
    private const string GeometryHexAlias = "__honua_geom_hex";
    private const string IdAlias = "__honua_id";
    private static readonly DatabricksSqlDialect Dialect = DatabricksSqlDialect.Instance;

    /// <inheritdoc />
    public DatabricksSqlStatement BuildSelect(DatabricksLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        EnsureSupported(query);

        var parameters = new List<DatabricksSqlParameter>();
        var sql = new StringBuilder("SELECT ");

        sql.Append(Dialect.QuoteIdentifier(mapping.PrimaryKeyColumn)).Append(" AS ").Append(IdAlias);
        sql.Append(", hex(st_asbinary(").Append(Dialect.QuoteIdentifier(mapping.GeometryColumn)).Append(")) AS ").Append(GeometryHexAlias);

        foreach (var column in ResolveAttributeColumns(mapping, query))
        {
            sql.Append(", ").Append(Dialect.QuoteIdentifier(column));
        }

        sql.Append(" FROM ").Append(mapping.QualifiedTable());
        AppendWhere(sql, mapping, query, parameters);
        AppendOrderBy(sql, mapping, query);
        AppendPaging(sql, query);

        return new DatabricksSqlStatement(sql.ToString(), parameters);
    }

    /// <inheritdoc />
    public DatabricksSqlStatement BuildCount(DatabricksLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        EnsureSupported(query);

        var parameters = new List<DatabricksSqlParameter>();
        var sql = new StringBuilder("SELECT COUNT(*) AS __honua_count FROM ");
        sql.Append(mapping.QualifiedTable());
        AppendWhere(sql, mapping, query, parameters);

        return new DatabricksSqlStatement(sql.ToString(), parameters);
    }

    /// <inheritdoc />
    public DatabricksSqlStatement BuildExtent(DatabricksLayerMapping mapping, FeatureQuery? query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var effectiveQuery = query ?? default;
        EnsureSupported(effectiveQuery);

        var parameters = new List<DatabricksSqlParameter>();
        var geom = Dialect.QuoteIdentifier(mapping.GeometryColumn);
        var sql = new StringBuilder("SELECT ");
        sql.Append("MIN(st_xmin(").Append(geom).Append(")) AS __honua_minx, ");
        sql.Append("MIN(st_ymin(").Append(geom).Append(")) AS __honua_miny, ");
        sql.Append("MAX(st_xmax(").Append(geom).Append(")) AS __honua_maxx, ");
        sql.Append("MAX(st_ymax(").Append(geom).Append(")) AS __honua_maxy");
        sql.Append(" FROM ").Append(mapping.QualifiedTable());
        AppendWhere(sql, mapping, effectiveQuery, parameters);

        return new DatabricksSqlStatement(sql.ToString(), parameters);
    }

    /// <inheritdoc />
    public DatabricksSqlStatement BuildObjectIds(DatabricksLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        EnsureSupported(query);

        var parameters = new List<DatabricksSqlParameter>();
        var sql = new StringBuilder("SELECT ");
        sql.Append(Dialect.QuoteIdentifier(mapping.PrimaryKeyColumn)).Append(" AS ").Append(IdAlias);
        sql.Append(" FROM ").Append(mapping.QualifiedTable());
        AppendWhere(sql, mapping, query, parameters);
        AppendOrderBy(sql, mapping, query);

        return new DatabricksSqlStatement(sql.ToString(), parameters);
    }

    /// <summary>Geometry-hex column alias produced by SELECT/extent statements.</summary>
    public static string GeometryHexColumn => GeometryHexAlias;

    /// <summary>Identifier column alias produced by SELECT statements.</summary>
    public static string IdColumn => IdAlias;

    private static IReadOnlyList<string> ResolveAttributeColumns(DatabricksLayerMapping mapping, FeatureQuery query)
    {
        if (query.ExcludeAttributes)
        {
            return [];
        }

        if (query.OutFields is { Length: > 0 } outFields)
        {
            // Only project configured attribute columns; ignore the primary key and
            // geometry (already projected) and anything not declared in the mapping.
            var requested = new List<string>(outFields.Length);
            foreach (var field in outFields)
            {
                if (field == "*")
                {
                    return mapping.AttributeColumns;
                }

                foreach (var column in mapping.AttributeColumns)
                {
                    if (column.Equals(field, StringComparison.OrdinalIgnoreCase) && !requested.Contains(column))
                    {
                        requested.Add(column);
                    }
                }
            }

            return requested;
        }

        return mapping.AttributeColumns;
    }

    private static void AppendWhere(
        StringBuilder sql,
        DatabricksLayerMapping mapping,
        FeatureQuery query,
        List<DatabricksSqlParameter> parameters)
    {
        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            // Forwarded as-is. Documented limitation: the GeoServices REST WHERE syntax
            // is assumed to be Spark-SQL compatible; the provider does not re-parse it.
            predicates.Add($"({query.Where})");
        }

        if (query.ObjectIds is { Length: > 0 } objectIds)
        {
            var markers = new List<string>(objectIds.Length);
            for (var i = 0; i < objectIds.Length; i++)
            {
                var name = $"oid{i}";
                markers.Add(":" + name);
                parameters.Add(new DatabricksSqlParameter(name, objectIds[i].ToString(CultureInfo.InvariantCulture), "BIGINT"));
            }

            predicates.Add($"{Dialect.QuoteIdentifier(mapping.PrimaryKeyColumn)} IN ({string.Join(", ", markers)})");
        }

        if (query.SpatialFilter is { IsSimpleEnvelope: true } spatial
            && spatial.EnvelopeMinX is double minX
            && spatial.EnvelopeMinY is double minY
            && spatial.EnvelopeMaxX is double maxX
            && spatial.EnvelopeMaxY is double maxY)
        {
            var geom = Dialect.QuoteIdentifier(mapping.GeometryColumn);
            predicates.Add(
                $"st_intersects({geom}, st_geomfromtext('POLYGON(({F(minX)} {F(minY)}, {F(maxX)} {F(minY)}, {F(maxX)} {F(maxY)}, {F(minX)} {F(maxY)}, {F(minX)} {F(minY)}))'))");
        }

        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }
    }

    private static void AppendOrderBy(StringBuilder sql, DatabricksLayerMapping mapping, FeatureQuery query)
    {
        if (query.OrderBy is not { Length: > 0 } orderBy)
        {
            return;
        }

        var clauses = new List<string>(orderBy.Length);
        foreach (var clause in orderBy)
        {
            DatabricksIdentifier.ValidateIdentifier(clause.Field);
            clauses.Add($"{Dialect.QuoteIdentifier(clause.Field)} {(clause.Ascending ? "ASC" : "DESC")}");
        }

        sql.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static void AppendPaging(StringBuilder sql, FeatureQuery query)
    {
        if (query.Limit is int limit && limit > 0)
        {
            sql.Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Offset is int offset && offset > 0)
        {
            sql.Append(" OFFSET ").Append(offset.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void EnsureSupported(FeatureQuery query)
    {
        if (query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "Databricks provider does not support EnforcedSqlFilter. Layers with server-enforced "
                + "definition or security filters cannot be served by this provider in this slice.");
        }

        if (query.SqlFilter is not null)
        {
            throw new NotSupportedException(
                "Databricks provider does not support parameterized SqlFilter. Use the Where property for filtering.");
        }

        if (query.TemporalFilter is not null)
        {
            throw new NotSupportedException(
                "Databricks provider does not support TemporalFilter in this slice.");
        }

        if (query.SpatialFilter is { IsSimpleEnvelope: false } spatial)
        {
            throw new NotSupportedException(
                $"Databricks provider does not support '{spatial.SpatialRelationship}' with a non-envelope geometry. "
                + "Only axis-aligned envelope filters (IsSimpleEnvelope=true) are translated in this slice.");
        }
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
