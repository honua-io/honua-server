// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;
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

    /// <summary>
    /// Builds an aggregate statistics query (<c>outStatistics</c> + optional
    /// <c>groupByFieldsForStatistics</c>) honoring the same WHERE/spatial filters as the SELECT.
    /// </summary>
    DatabricksSqlStatement BuildStatistics(DatabricksLayerMapping mapping, FeatureQuery query);
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

                foreach (var column in mapping.AttributeColumns.Where(column =>
                    column.Equals(field, StringComparison.OrdinalIgnoreCase) && !requested.Contains(column)))
                {
                    requested.Add(column);
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

        var attributePredicate = TranslateWhere(mapping, query, parameters);
        if (attributePredicate is not null)
        {
            predicates.Add($"({attributePredicate})");
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

    /// <inheritdoc />
    public DatabricksSqlStatement BuildStatistics(DatabricksLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        EnsureSupported(query);

        if (!query.OutStatistics.HasValue || query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            // No aggregates requested: return an empty result rather than a malformed SELECT.
            return DatabricksSqlStatement.WithoutParameters("SELECT 1 WHERE FALSE");
        }

        var parameters = new List<DatabricksSqlParameter>();
        var sql = new StringBuilder("SELECT ");

        var groupByColumns = BuildGroupByColumns(query);
        if (groupByColumns.Count > 0)
        {
            sql.Append(string.Join(", ", groupByColumns)).Append(", ");
        }

        sql.Append(BuildStatisticColumns(query.OutStatistics.Value));
        sql.Append(" FROM ").Append(mapping.QualifiedTable());
        AppendWhere(sql, mapping, query, parameters);

        if (groupByColumns.Count > 0)
        {
            sql.Append(" GROUP BY ").Append(string.Join(", ", groupByColumns));
        }

        return new DatabricksSqlStatement(sql.ToString(), parameters);
    }

    private static List<string> BuildGroupByColumns(FeatureQuery query)
    {
        var columns = new List<string>();
        if (query.GroupByFields is { Length: > 0 } groupBy)
        {
            foreach (var field in groupBy)
            {
                DatabricksIdentifier.ValidateIdentifier(field);
                columns.Add(Dialect.QuoteIdentifier(field));
            }
        }

        return columns;
    }

    private static string BuildStatisticColumns(ImmutableArray<StatisticDefinition> statistics)
    {
        var columns = new List<string>(statistics.Length);
        foreach (var stat in statistics)
        {
            DatabricksIdentifier.ValidateIdentifier(stat.OnStatisticField);
            DatabricksIdentifier.ValidateIdentifier(stat.OutStatisticFieldName);
            var fieldExpr = Dialect.QuoteIdentifier(stat.OnStatisticField);
            var alias = Dialect.QuoteIdentifier(stat.OutStatisticFieldName);
            var statExpr = stat.StatisticType switch
            {
                StatisticType.Count => $"COUNT({fieldExpr})",
                StatisticType.Sum => $"SUM({fieldExpr})",
                StatisticType.Min => $"MIN({fieldExpr})",
                StatisticType.Max => $"MAX({fieldExpr})",
                StatisticType.Avg => $"AVG({fieldExpr})",
                // Spark SQL exposes sample stddev/variance as STDDEV_SAMP / VAR_SAMP.
                StatisticType.Stddev => $"STDDEV_SAMP({fieldExpr})",
                StatisticType.Var => $"VAR_SAMP({fieldExpr})",
                _ => throw new NotSupportedException(
                    $"Statistic type '{stat.StatisticType}' is not supported by the Databricks provider."),
            };
            columns.Add($"{statExpr} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    /// <summary>
    /// Translates the canonical <see cref="FeatureQuery.Where"/> clause into a parameterized
    /// Spark-SQL predicate. The GeoServices REST where string is parsed into the shared filter
    /// AST and walked by <see cref="DatabricksSqlFilterTranslator"/>; translated literal operands
    /// are appended to <paramref name="parameters"/> as <c>:pN</c> bindings. Returns null when no
    /// filter is present. A Postgres-flavored <see cref="FeatureQuery.SqlFilter"/> (produced by the
    /// shared translator pipeline) is ignored in favor of re-parsing the canonical Where text.
    /// </summary>
    private static string? TranslateWhere(
        DatabricksLayerMapping mapping,
        FeatureQuery query,
        List<DatabricksSqlParameter> parameters)
    {
        if (string.IsNullOrWhiteSpace(query.Where))
        {
            return null;
        }

        FilterExpression expression;
        try
        {
            expression = new GeoServicesSqlParser().Parse(query.Where);
        }
        catch (ArgumentException ex)
        {
            throw new NotSupportedException(
                $"Databricks provider could not translate the where clause: {ex.Message}", ex);
        }

        var context = BuildTranslationContext(mapping);
        var translator = new DatabricksSqlFilterTranslator();
        var fragment = translator.Translate(expression, context);

        // Re-base the translated :pN markers onto the statement's running parameter sequence so
        // they never collide with object-id (:oidN) bindings emitted elsewhere in the WHERE.
        return RebindParameters(fragment, parameters);
    }

    private static string RebindParameters(SqlFragment fragment, List<DatabricksSqlParameter> parameters)
    {
        var sql = fragment.Sql;
        for (var i = 0; i < fragment.Parameters.Count; i++)
        {
            var name = $"f{parameters.Count}";
            // Replace the translator's positional :pN marker with the statement-scoped :fN marker.
            sql = sql.Replace($":p{i}", $":{name}", StringComparison.Ordinal);
            var (rendered, type) = RenderParameter(fragment.Parameters[i]);
            parameters.Add(new DatabricksSqlParameter(name, rendered, type));
        }

        return sql;
    }

    private static (string? Value, string? Type) RenderParameter(object? value) => value switch
    {
        null => (null, null),
        bool b => (b ? "true" : "false", "BOOLEAN"),
        long l => (l.ToString(CultureInfo.InvariantCulture), "BIGINT"),
        int n => (n.ToString(CultureInfo.InvariantCulture), "INT"),
        short s => (s.ToString(CultureInfo.InvariantCulture), "SMALLINT"),
        double d => (d.ToString("R", CultureInfo.InvariantCulture), "DOUBLE"),
        float f => (f.ToString("R", CultureInfo.InvariantCulture), "DOUBLE"),
        decimal m => (m.ToString(CultureInfo.InvariantCulture), "DECIMAL(38,18)"),
        DateTimeOffset dto => (dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "TIMESTAMP"),
        DateTime dt => (dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "TIMESTAMP"),
        _ => (value.ToString(), "STRING"),
    };

    private static FilterTranslationContext BuildTranslationContext(DatabricksLayerMapping mapping)
    {
        var fields = new List<FilterTranslationContext.ContextField>(mapping.AttributeColumns.Count + 2)
        {
            new(mapping.PrimaryKeyColumn, MetadataV2FieldType.BigInteger, IsGeometry: false, IsPrimaryKey: true),
            new(mapping.GeometryColumn, MetadataV2FieldType.Geometry, IsGeometry: true, IsPrimaryKey: false),
        };

        foreach (var column in mapping.AttributeColumns)
        {
            fields.Add(new FilterTranslationContext.ContextField(
                column, MetadataV2FieldType.String, IsGeometry: false, IsPrimaryKey: false));
        }

        return FilterTranslationContext.FromColumns(
            fields,
            mapping.PrimaryKeyColumn,
            mapping.GeometryColumn,
            mapping.Srid,
            mapping.GeometryType,
            $"layer {mapping.LayerId}");
    }

    private static void EnsureSupported(FeatureQuery query)
    {
        if (query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "Databricks provider does not support EnforcedSqlFilter. Layers with server-enforced "
                + "definition or security filters cannot be served by this provider in this slice.");
        }

        // The shared filter pipeline emits a Postgres-flavored SqlFilter alongside the canonical
        // Where text. We re-parse Where ourselves into Spark SQL, so a translated SqlFilter is only
        // a problem when there is no Where to fall back to (it cannot be executed against DBSQL).
        if (query.SqlFilter is not null && string.IsNullOrWhiteSpace(query.Where))
        {
            throw new NotSupportedException(
                "Databricks provider cannot execute a pre-translated SqlFilter without the canonical "
                + "Where text. The shared translator emits Postgres-flavored SQL; route the request "
                + "through a path that preserves the GeoServices 'where' clause.");
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
