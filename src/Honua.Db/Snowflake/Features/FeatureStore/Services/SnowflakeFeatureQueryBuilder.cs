// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Snowflake.Features.FeatureStore.Services;

internal static partial class SnowflakeFeatureQueryBuilder
{
    public static ParameterizedQuery BuildSelectQuery(SnowflakeLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);
        GuardUnsupportedTemporalFilter(query);
        GuardUnsupportedOutputSrid(mapping, query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");

        var geometryExpr = BuildGeometryWkbExpression(mapping);
        sb.Append(", ").Append(geometryExpr).Append(" AS \"__geometry\"");

        AppendAttributeColumns(sb, query, attributeColumns);

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, mapping, attributeColumns, query);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildCountQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT COUNT(*) FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildObjectIdsQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, mapping, attributeColumns, query);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildExtentQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery? query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            throw new InvalidOperationException(
                $"Layer {mapping.LayerId} has no geometry column configured; extent is not available.");
        }

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var geometryColumn = mapping.QuotedGeometryColumn!;

        sb.Append("SELECT ");
        sb.Append("MIN(ST_XMIN(").Append(geometryColumn).Append(")) AS \"min_x\", ");
        sb.Append("MIN(ST_YMIN(").Append(geometryColumn).Append(")) AS \"min_y\", ");
        sb.Append("MAX(ST_XMAX(").Append(geometryColumn).Append(")) AS \"max_x\", ");
        sb.Append("MAX(ST_YMAX(").Append(geometryColumn).Append(")) AS \"max_y\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geometryColumn).Append(" IS NOT NULL");

        var effective = query ?? new FeatureQuery();
        GuardUnsupportedTemporalFilter(effective);
        GuardUnsupportedOutputSrid(mapping, effective);
        AppendWhereClause(sb, mapping, attributeColumns, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildStatistics(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        if (query.OutStatistics is not { IsDefaultOrEmpty: false } statistics)
        {
            // No aggregates requested: return an empty result rather than a malformed SELECT.
            return new ParameterizedQuery("SELECT 1 WHERE FALSE", new List<object>());
        }

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var resolvedGroupByColumns = ResolveGroupByColumns(mapping, attributeColumns, query);
        var groupByColumns = resolvedGroupByColumns.Select(SnowflakeIdentifier.QuoteAttribute).ToList();

        sb.Append("SELECT ");
        if (groupByColumns.Count > 0)
        {
            sb.Append(string.Join(", ", groupByColumns)).Append(", ");
        }

        sb.Append(BuildStatisticColumns(mapping, attributeColumns, statistics));
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);

        if (groupByColumns.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", groupByColumns));
        }

        AppendStatisticsHaving(sb, mapping, attributeColumns, query, parameters);
        var hasOrderBy = query.OrderBy is { IsDefaultOrEmpty: false };
        RelationalFeatureStoreHelpers.AppendStatisticsOrderBy(
            sb,
            statistics,
            resolvedGroupByColumns,
            query,
            SnowflakeIdentifier.QuoteAttribute);
        // LIMIT / OFFSET are inline integer literals, matching SELECT, so positional ? binds
        // stay aligned with WhereParameters. OFFSET without ORDER BY is rejected by Snowflake,
        // and the primary key is not in the grouped projection, so fall back to the first
        // aggregate output column.
        var fallbackOrder = groupByColumns.Count > 0
            ? groupByColumns[0]
            : SnowflakeIdentifier.QuoteAttribute(statistics[0].OutStatisticFieldName);
        AppendStatisticsPagination(sb, query, fallbackOrder, hasOrderBy);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildH3AggregationQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        h3Query.RejectSummaryDefinitions();

        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            throw new InvalidOperationException(
                $"Layer {mapping.LayerId} has no geometry column configured; H3 aggregation is not available.");
        }

        if (h3Query.Resolution is < H3AggregationQuery.MinResolution or > H3AggregationQuery.MaxResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(h3Query),
                $"H3 resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}.");
        }

        if (h3Query.KRingDistance is < 0 or > H3AggregationQuery.MaxKRingDistance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(h3Query),
                $"H3 k-ring distance must be between 0 and {H3AggregationQuery.MaxKRingDistance}.");
        }

        if (mapping.GeometryColumnType == SnowflakeGeometryColumnType.Geometry
            && mapping.Srid is > 0 and not 4326)
        {
            throw new NotSupportedException(
                $"Snowflake H3 aggregation requires WGS 84 (EPSG:4326) geometry or GEOGRAPHY; layer {mapping.LayerId} uses SRID {mapping.Srid.Value}.");
        }

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var geomCol = mapping.QuotedGeometryColumn!;
        var polyfillCteLength = 0;

        if (h3Query.PolyfillGeometry is { Length: > 0 })
        {
            if (h3Query.PolyfillSrid is > 0 and not 4326)
            {
                throw new NotSupportedException(
                    $"Snowflake H3 polyfill requires WGS 84 (EPSG:4326) geometry; received SRID {h3Query.PolyfillSrid.Value}.");
            }

            parameters.Add(h3Query.PolyfillGeometry);
            parameters.Add(h3Query.Resolution);
            sb.Append("WITH polyfill_cells AS (SELECT VALUE::VARCHAR AS cell FROM TABLE(FLATTEN(INPUT => H3_POLYGON_TO_CELLS_STRINGS(TO_GEOGRAPHY(?, TRUE), ?))))");
            polyfillCteLength = sb.Length;
            sb.Append(' ');
        }

        parameters.Add(h3Query.Resolution);
        var cellExpr = mapping.GeometryColumnType == SnowflakeGeometryColumnType.Geography
            ? $"H3_POINT_TO_CELL_STRING(ST_CENTROID({geomCol}), ?)"
            : $"H3_LATLNG_TO_CELL_STRING(ST_Y(ST_CENTROID({geomCol})), ST_X(ST_CENTROID({geomCol})), ?)";

        sb.Append("SELECT _h3cell AS \"cellIndex\", ST_ASGEOJSON(H3_CELL_TO_BOUNDARY(_h3cell)) AS \"cellGeometry\"");
        if (h3Query.OutStatistics is { IsDefaultOrEmpty: false } stats)
        {
            sb.Append(", ").Append(BuildStatisticColumns(mapping, attributeColumns, stats));
        }
        else
        {
            sb.Append(", COUNT(").Append(mapping.QuotedPrimaryKeyColumn).Append(") AS \"count\"");
        }

        sb.Append(" FROM (SELECT *, ").Append(cellExpr).Append(" AS _h3cell FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geomCol).Append(" IS NOT NULL");
        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        sb.Append(") _src");

        if (polyfillCteLength > 0)
        {
            sb.Append(" WHERE _h3cell IN (SELECT cell FROM polyfill_cells)");
        }

        sb.Append(" GROUP BY _h3cell");

        if (h3Query.KRingDistance is > 0)
        {
            var innerSql = sb.ToString();
            sb.Clear();
            if (polyfillCteLength > 0)
            {
                sb.Append(innerSql[..polyfillCteLength]);
                sb.Append(", aggregated AS (");
                sb.Append(innerSql[(polyfillCteLength + 1)..]);
            }
            else
            {
                sb.Append("WITH aggregated AS (");
                sb.Append(innerSql);
            }

            parameters.Add(h3Query.KRingDistance.Value);
            sb.Append(") SELECT k.VALUE::VARCHAR AS \"cellIndex\", ST_ASGEOJSON(H3_CELL_TO_BOUNDARY(k.VALUE::VARCHAR)) AS \"cellGeometry\"");
            AppendH3KRingReaggregateColumns(sb, h3Query);
            sb.Append(" FROM aggregated a, LATERAL FLATTEN(INPUT => H3_GRID_DISK(a.\"cellIndex\", ?)) k GROUP BY k.VALUE::VARCHAR");
        }
        else
        {
            sb.Append(" ORDER BY COUNT(*) DESC");
        }

        if (h3Query.MaxCells is > 0)
        {
            sb.Append(" LIMIT ").Append(h3Query.MaxCells.Value.ToString(CultureInfo.InvariantCulture));
        }

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static void AppendH3KRingReaggregateColumns(StringBuilder sb, H3AggregationQuery h3Query)
    {
        if (h3Query.OutStatistics is not { IsDefaultOrEmpty: false } stats)
        {
            sb.Append(", SUM(a.\"count\") AS \"count\"");
            return;
        }

        foreach (var stat in stats)
        {
            var alias = SnowflakeIdentifier.QuoteAttribute(stat.OutStatisticFieldName);
            var agg = stat.StatisticType switch
            {
                StatisticType.Count or StatisticType.Sum => $"SUM(a.{alias})",
                StatisticType.Min => $"MIN(a.{alias})",
                StatisticType.Max => $"MAX(a.{alias})",
                _ => $"AVG(a.{alias})",
            };
            sb.Append(", ").Append(agg).Append(" AS ").Append(alias);
        }
    }

    private static List<string> ResolveGroupByColumns(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query)
    {
        if (query.GroupByFields is not { IsDefaultOrEmpty: false } groupBy)
        {
            return [];
        }

        var columns = new List<string>(groupBy.Length);
        foreach (var field in groupBy)
        {
            _ = SnowflakeIdentifier.QuoteAttribute(field);
            columns.Add(ResolveDeclaredField(mapping, attributeColumns, field, "GROUP BY"));
        }

        return columns;
    }

    private static string BuildStatisticColumns(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        ImmutableArray<StatisticDefinition> statistics)
    {
        var columns = new List<string>(statistics.Length);
        foreach (var stat in statistics)
        {
            _ = SnowflakeIdentifier.QuoteAttribute(stat.OnStatisticField);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, stat.OnStatisticField, "OutStatistics");
            var fieldExpr = SnowflakeIdentifier.QuoteAttribute(resolved);
            var alias = SnowflakeIdentifier.QuoteAttribute(stat.OutStatisticFieldName);
            columns.Add($"{StatisticExpression(stat.StatisticType, fieldExpr)} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    private static void AppendStatisticsHaving(
        StringBuilder sb,
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query,
        List<object> parameters)
    {
        if (query.Having is not { IsDefaultOrEmpty: false } having)
        {
            return;
        }

        sb.Append(" HAVING ");
        for (var i = 0; i < having.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(" AND ");
            }

            var condition = having[i];
            _ = SnowflakeIdentifier.QuoteAttribute(condition.OnStatisticField);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, condition.OnStatisticField, "HAVING");
            var fieldExpr = SnowflakeIdentifier.QuoteAttribute(resolved);
            parameters.Add(condition.BoundValue());
            sb.Append(StatisticExpression(condition.StatisticType, fieldExpr))
                .Append(' ')
                .Append(HavingOperator(condition.Operator))
                .Append(" ?");
        }
    }

    private static void AppendStatisticsPagination(
        StringBuilder sb,
        FeatureQuery query,
        string fallbackOrderColumn,
        bool hasOrderBy)
    {
        var offset = query.Offset.GetValueOrDefault(0);
        if (!query.Limit.HasValue && offset <= 0)
        {
            return;
        }

        if (!hasOrderBy && offset > 0)
        {
            sb.Append(" ORDER BY ").Append(fallbackOrderColumn);
        }

        if (query.Limit.HasValue)
        {
            sb.Append(" LIMIT ").Append(query.Limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (offset > 0)
        {
            sb.Append(" OFFSET ").Append(offset.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string StatisticExpression(StatisticType statisticType, string fieldExpr)
    {
        // STDDEV_SAMP / VAR_SAMP are the sample aggregates named by StatisticType.
        return statisticType switch
        {
            StatisticType.Count => $"COUNT({fieldExpr})",
            StatisticType.Sum => $"SUM({fieldExpr})",
            StatisticType.Min => $"MIN({fieldExpr})",
            StatisticType.Max => $"MAX({fieldExpr})",
            StatisticType.Avg => $"AVG({fieldExpr})",
            StatisticType.Stddev => $"STDDEV_SAMP({fieldExpr})",
            StatisticType.Var => $"VAR_SAMP({fieldExpr})",
            _ => throw new NotSupportedException(
                $"Statistic type '{statisticType}' is not supported by the Snowflake provider."),
        };
    }

    private static string HavingOperator(HavingComparisonOperator op)
        => op switch
        {
            HavingComparisonOperator.Equal => "=",
            HavingComparisonOperator.NotEqual => "<>",
            HavingComparisonOperator.GreaterThan => ">",
            HavingComparisonOperator.GreaterThanOrEqual => ">=",
            HavingComparisonOperator.LessThan => "<",
            HavingComparisonOperator.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException(
                $"Having operator '{op}' is not supported by the Snowflake provider."),
        };

    private static void GuardUnsupportedTemporalFilter(FeatureQuery query)
    {
        if (query.TemporalFilter.HasValue)
        {
            throw new NotSupportedException(
                "Temporal filters are not supported by the Snowflake provider in this slice. " +
                "Apply temporal filtering in the calling layer or use a PostGIS-backed layer.");
        }
    }

    private static void GuardUnsupportedOutputSrid(SnowflakeLayerMapping mapping, FeatureQuery query)
    {
        if (query.OutputSrid is > 0 && mapping.Srid is > 0 && query.OutputSrid.Value != mapping.Srid.Value)
        {
            throw new NotSupportedException(
                $"Output SRID transforms are not supported by the Snowflake provider " +
                $"(layer SRID is {mapping.Srid.Value}, requested {query.OutputSrid.Value}). " +
                "Request features in the layer's storage SRID or use a PostGIS-backed layer.");
        }
    }

    private static string BuildGeometryWkbExpression(SnowflakeLayerMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return "CAST(NULL AS BINARY)";
        }

        // ST_ASWKB emits OGC 2D WKB; any Z/M ordinates on the source geometry are dropped
        // (documented limitation matching the other relational providers).
        return $"ST_ASWKB({mapping.QuotedGeometryColumn})";
    }

    private static void AppendAttributeColumns(StringBuilder sb, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        if (query.ExcludeAttributes || attributeColumns.Count == 0)
        {
            return;
        }

        // Null/default OutFields projects every column. An explicit empty array projects
        // none. IsEmpty throws on a default ImmutableArray, so test IsDefault first.
        IEnumerable<string> columns = attributeColumns;
        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefault)
        {
            var requestedOutFields = query.OutFields.Value;
            if (requestedOutFields.IsEmpty)
            {
                return;
            }

            var requested = new HashSet<string>(requestedOutFields, StringComparer.OrdinalIgnoreCase);
            columns = attributeColumns.Where(c => requested.Contains(c));
        }

        foreach (var column in columns)
        {
            sb.Append(", ").Append(SnowflakeIdentifier.QuoteAttribute(column));
        }
    }

    private static void AppendWhereClause(
        StringBuilder sb,
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query,
        List<object> parameters)
    {
        if (query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "Snowflake provider does not support EnforcedSqlFilter. Layers with server-enforced " +
                "definition or security filters cannot be served by this provider in this slice.");
        }

        if (query.TextSearch is { } search)
        {
            var predicate = FeatureTextSearchSql.Build(search, field =>
            {
                _ = SnowflakeIdentifier.QuoteAttribute(field);
                var resolved = ResolveDeclaredField(mapping, attributeColumns, field, "TextSearch");
                return SnowflakeIdentifier.QuoteAttribute(resolved);
            }, text =>
            {
                var marker = "?";
                parameters.Add(text);
                return marker;
            }, (column, value) => $"POSITION({value}, {column})");
            sb.Append(" AND ").Append(predicate);
        }

        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres translator today.
        // Passing a Postgres-emitted fragment through here would yield invalid Snowflake SQL, so
        // we re-parse Where using this provider's own Snowflake-aware parser whenever it is supplied.
        // A supplemental fragment (time filter or object ids that were not forwarded) is not
        // represented by Where; executing Where alone would drop those predicates.
        if (query.SqlFilterSupplementsWhere)
        {
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the Snowflake provider. " +
                "SqlFilter supplements Where, and the shared ISqlFilterTranslator pipeline emits Postgres-flavored " +
                "SQL which is not valid Snowflake SQL. The supplemental predicates would be dropped if Where " +
                "were re-parsed alone.");
        }

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), mapping, attributeColumns, parameters);
            sb.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (query.SqlFilter is not null)
        {
            // The shared ISqlFilterTranslator pipeline registers a PostgreSQL translator; the
            // SqlFilter it produces contains Postgres-specific SQL (JSONB ->> operators, ::casts,
            // ST_* signatures, double-quoted identifiers) that is not valid Snowflake SQL. The
            // SqlServer/Oracle providers document and enforce the same restriction. Reject here so
            // callers receive a clear, actionable error instead of an opaque driver exception.
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the Snowflake provider. " +
                "The shared ISqlFilterTranslator pipeline emits Postgres-flavored SQL which is not valid Snowflake SQL. " +
                "Route the request through a protocol path that populates the canonical Where text (FeatureServer 'where'), " +
                "or restrict CQL2/FES/OData $filter usage to providers that register their own ISqlFilterTranslator.");
        }
    }

    private static void AppendObjectIdsFilter(StringBuilder sb, SnowflakeLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.ObjectIds.HasValue || query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var ids = query.ObjectIds.Value;
        if (ids.Length > 16_000)
        {
            throw new NotSupportedException(
                $"The object id list of {ids.Length.ToString(CultureInfo.InvariantCulture)} values exceeds this provider's parameter limit of 16000.");
        }

        var placeholders = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders[i] = "?";
            parameters.Add(ids[i]);
        }

        sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn).Append(" IN (").Append(string.Join(", ", placeholders)).Append(')');
    }

    private static void AppendSpatialFilter(StringBuilder sb, SnowflakeLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.SpatialFilter.HasValue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            throw new InvalidOperationException(
                $"Layer {mapping.LayerId} has no geometry column configured; spatial filters are not supported.");
        }

        var filter = query.SpatialFilter.Value;
        var geomCol = mapping.QuotedGeometryColumn!;
        var filterExpr = BuildFilterGeometryExpression(filter, mapping, parameters);

        // Snowflake does not expose an envelope-only intersection predicate; treat
        // EnvelopeIntersects as a windowing path that maps to ST_INTERSECTS (envelope-vs-geometry
        // intersection is a superset of geometry-vs-geometry, which is acceptable for viewport
        // windowing). All other relationships map to their exact ST_* function.
        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects or SpatialRelationship.EnvelopeIntersects =>
                $"ST_INTERSECTS({geomCol}, {filterExpr})",
            // Esri semantics: esriSpatialRelWithin = filter geometry is within feature geometry;
            // esriSpatialRelContains = filter geometry contains feature geometry. Lead with the
            // filter geometry to match the canonical PostGIS reference. Reversing the
            // operands inverts the relationship and returns the wrong (typically empty) result set.
            SpatialRelationship.Within =>
                $"ST_WITHIN({filterExpr}, {geomCol})",
            SpatialRelationship.Contains =>
                $"ST_CONTAINS({filterExpr}, {geomCol})",
            SpatialRelationship.Disjoint =>
                $"ST_DISJOINT({geomCol}, {filterExpr})",
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the Snowflake provider in this slice.")
        };

        sb.Append(" AND ").Append(clause);
    }

    private static string BuildFilterGeometryExpression(SpatialFilter filter, SnowflakeLayerMapping mapping, List<object> parameters)
    {
        // Snowflake ST_* relationship functions require both operands to share a spatial reference.
        // Mirror the SqlServer/MySQL contract: reject cross-SRID filters with a clear error rather
        // than returning a silently empty result set.
        if (filter.Srid is > 0 && mapping.Srid.HasValue && filter.Srid.Value != mapping.Srid.Value)
        {
            throw new NotSupportedException(
                $"Cross-SRID spatial filter is not supported by the Snowflake provider: " +
                $"filter SRID {filter.Srid.Value} differs from layer SRID {mapping.Srid.Value}. " +
                $"Pre-project the filter geometry to SRID {mapping.Srid.Value} before submitting the request.");
        }

        parameters.Add(WkbSridNormalizer.RemoveEmbeddedSrid(filter.Geometry));

        if (mapping.GeometryColumnType == SnowflakeGeometryColumnType.Geometry && mapping.Srid is > 0)
        {
            return $"TO_GEOMETRY(?, {mapping.Srid.Value.ToString(CultureInfo.InvariantCulture)}, TRUE)";
        }

        // TO_GEOGRAPHY/TO_GEOMETRY parse a WKB byte string when the trailing argument is true.
        return $"{mapping.SpatialConstructor}(?, TRUE)";
    }

    private static void AppendOrderByClause(
        StringBuilder sb,
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query)
    {
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var clauses = new List<string>(query.OrderBy.Value.Length);
        foreach (var orderBy in query.OrderBy.Value)
        {
            _ = SnowflakeIdentifier.QuoteAttribute(orderBy.Field);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, orderBy.Field, "ORDER BY");
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{SnowflakeIdentifier.QuoteAttribute(resolved)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static void AppendPagination(StringBuilder sb, SnowflakeLayerMapping mapping, FeatureQuery query)
    {
        if (!query.Limit.HasValue && (!query.Offset.HasValue || query.Offset.Value <= 0))
        {
            return;
        }

        // Snowflake requires an ORDER BY for deterministic OFFSET paging. When the caller did not
        // supply one, fall back to the primary key so paging is stable across pages. Pagination
        // counts are emitted as inline integer literals (never user-supplied strings), so this
        // remains injection-safe while keeping the positional ? binds aligned with WhereParameters.
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            sb.Append(" ORDER BY ").Append(mapping.QuotedPrimaryKeyColumn);
        }

        if (query.Limit.HasValue)
        {
            sb.Append(" LIMIT ").Append(query.Limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        var offset = query.Offset.GetValueOrDefault(0);
        if (offset > 0)
        {
            sb.Append(" OFFSET ").Append(offset.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string ParseAndParameterizeWhereClause(
        string whereClause,
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        List<object> parameters)
    {
        var rendered = new List<string>();
        foreach (var expression in LegacyWhereClauseParser.Parse(WhereClauseSplitter.SplitOnAnd(whereClause), LegacyWhereGrammar.Warehouse))
        {
            if (expression.Kind == LegacyWhereExpressionKind.True)
            {
                rendered.Add("1=1");
                continue;
            }

            var nullMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.NullCheck)
            {
                var field = nullMatch.Groups["field"].Value;
                _ = SnowflakeIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{SnowflakeIdentifier.QuoteAttribute(field)} IS {notClause}NULL");
                continue;
            }

            var inMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.In)
            {
                var field = inMatch.Groups["field"].Value;
                _ = SnowflakeIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var placeholders = new List<string>();
                foreach (Match valueMatch in LegacyWhereClauseParser.MatchInValues(inMatch.Groups["values"].Value))
                {
                    placeholders.Add("?");
                    parameters.Add(LegacyWhereClauseParser.ParseValueToken(valueMatch.Value, LegacyWhereValuePolicy.NumericRequired));
                }
                rendered.Add($"{SnowflakeIdentifier.QuoteAttribute(field)} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            var compMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.Comparison)
            {
                var field = compMatch.Groups["field"].Value;
                _ = SnowflakeIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var op = LegacyWhereClauseParser.NormalizeOperator(compMatch.Groups["op"].Value, LegacyWhereGrammar.Warehouse);
                var value = LegacyWhereClauseParser.ParseValueToken(compMatch.Groups["value"].Value, LegacyWhereValuePolicy.NumericRequired);

                parameters.Add(value);
                rendered.Add($"{SnowflakeIdentifier.QuoteAttribute(field)} {op} ?");
                continue;
            }

            throw new ArgumentException("WHERE clause format not supported.");
        }

        return string.Join(" AND ", rendered);
    }

    private static string ResolveDeclaredField(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        string field,
        string clause)
    {
        if (mapping.PrimaryKeyColumn.Equals(field, StringComparison.OrdinalIgnoreCase))
        {
            return mapping.PrimaryKeyColumn;
        }

        if (mapping.GeometryColumn is { } geometryColumn &&
            geometryColumn.Equals(field, StringComparison.OrdinalIgnoreCase))
        {
            return geometryColumn;
        }

        if (attributeColumns is null)
        {
            return field;
        }

        var declared = attributeColumns.FirstOrDefault(column => column.Equals(field, StringComparison.OrdinalIgnoreCase));
        return declared ?? throw new ArgumentException(
            $"{clause} field '{field}' is not declared on layer {mapping.LayerId}.");
    }

}
