// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Snowflake.Features.FeatureStore.Services;

internal static partial class SnowflakeFeatureQueryBuilder
{
    private sealed class SnowflakeQueryContext
    {
        public required StringBuilder Sql { get; init; }
        public required SnowflakeLayerMapping Mapping { get; init; }
        public IReadOnlyList<string>? AttributeColumns { get; init; }
        public required FeatureQuery Query { get; init; }
        public required List<object> Parameters { get; init; }

        public DeclaredFieldScope CreateFieldScope()
            => SnowflakeFeatureQueryBuilder.CreateFieldScope(Mapping, AttributeColumns);

        public string AddParameter(object value)
        {
            Parameters.Add(value);
            return "?";
        }
    }

    public static ParameterizedQuery BuildSelectQuery(SnowflakeLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);
        GuardUnsupportedTemporalFilter(query);
        GuardUnsupportedOutputSrid(mapping, query);

        var context = CreateQueryContext(mapping, attributeColumns, query);
        var sb = context.Sql;

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");

        var geometryExpr = BuildGeometryWkbExpression(mapping);
        sb.Append(", ").Append(geometryExpr).Append(" AS \"__geometry\"");

        AppendAttributeColumns(sb, query, attributeColumns);

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(context);
        AppendObjectIdsFilter(sb, mapping, query, context.Parameters);
        AppendSpatialFilter(sb, mapping, query, context.Parameters);
        AppendOrderByClause(context);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), context.Parameters);
    }

    public static ParameterizedQuery BuildCountQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var context = CreateQueryContext(mapping, attributeColumns, query);
        var sb = context.Sql;

        sb.Append("SELECT COUNT(*) FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(context);
        AppendObjectIdsFilter(sb, mapping, query, context.Parameters);
        AppendSpatialFilter(sb, mapping, query, context.Parameters);

        return new ParameterizedQuery(sb.ToString(), context.Parameters);
    }

    public static ParameterizedQuery BuildObjectIdsQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var context = CreateQueryContext(mapping, attributeColumns, query);
        var sb = context.Sql;

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(context);
        AppendObjectIdsFilter(sb, mapping, query, context.Parameters);
        AppendSpatialFilter(sb, mapping, query, context.Parameters);
        AppendOrderByClause(context);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), context.Parameters);
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

        var effective = query ?? new FeatureQuery();
        GuardUnsupportedTemporalFilter(effective);
        GuardUnsupportedOutputSrid(mapping, effective);

        var context = CreateQueryContext(mapping, attributeColumns, effective);
        var sb = context.Sql;
        var geometryColumn = mapping.QuotedGeometryColumn!;

        sb.Append("SELECT ");
        sb.Append("MIN(ST_XMIN(").Append(geometryColumn).Append(")) AS \"min_x\", ");
        sb.Append("MIN(ST_YMIN(").Append(geometryColumn).Append(")) AS \"min_y\", ");
        sb.Append("MAX(ST_XMAX(").Append(geometryColumn).Append(")) AS \"max_x\", ");
        sb.Append("MAX(ST_YMAX(").Append(geometryColumn).Append(")) AS \"max_y\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geometryColumn).Append(" IS NOT NULL");

        AppendWhereClause(context);
        AppendObjectIdsFilter(sb, mapping, effective, context.Parameters);
        AppendSpatialFilter(sb, mapping, effective, context.Parameters);

        return new ParameterizedQuery(sb.ToString(), context.Parameters);
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

        var context = CreateQueryContext(mapping, attributeColumns, query);
        var sb = context.Sql;
        var resolvedGroupByColumns = ResolveGroupByColumns(mapping, attributeColumns, query);
        var groupByColumns = resolvedGroupByColumns.Select(SnowflakeIdentifier.QuoteAttribute).ToList();

        RelationalFeatureStoreHelpers.AppendStatisticsBody(new()
        {
            Sql = sb,
            QuotedTableReference = mapping.QuotedTableReference,
            QuotedGroupByColumns = groupByColumns,
            StatisticColumnsSql = BuildStatisticColumns(mapping, attributeColumns, statistics),
            AppendFilters = () =>
            {
                AppendWhereClause(context);
                AppendObjectIdsFilter(sb, mapping, query, context.Parameters);
                AppendSpatialFilter(sb, mapping, query, context.Parameters);
            },
        });

        AppendStatisticsHaving(context);
        var hasOrderBy = query.OrderBy is { IsDefaultOrEmpty: false };
        RelationalFeatureStoreHelpers.AppendStatisticsOrderBy(new() { Sql = sb, Statistics = statistics, ResolvedGroupByColumns = resolvedGroupByColumns, Query = query, QuoteAttribute = SnowflakeIdentifier.QuoteAttribute });
        // LIMIT / OFFSET are inline integer literals, matching SELECT, so positional ? binds
        // stay aligned with WhereParameters. OFFSET without ORDER BY is rejected by Snowflake,
        // and the primary key is not in the grouped projection, so fall back to the first
        // aggregate output column.
        var fallbackOrder = groupByColumns.Count > 0
            ? groupByColumns[0]
            : SnowflakeIdentifier.QuoteAttribute(statistics[0].OutStatisticFieldName);
        AppendStatisticsPagination(sb, query, fallbackOrder, hasOrderBy);

        return new ParameterizedQuery(sb.ToString(), context.Parameters);
    }

    private static SnowflakeQueryContext CreateQueryContext(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query)
        => new()
        {
            Sql = new StringBuilder(),
            Mapping = mapping,
            AttributeColumns = attributeColumns,
            Query = query,
            Parameters = new List<object>(),
        };

    private static DeclaredFieldScope CreateFieldScope(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns)
        => new()
        {
            LayerId = mapping.LayerId,
            PrimaryKeyColumn = mapping.PrimaryKeyColumn,
            GeometryColumn = mapping.GeometryColumn,
            AttributeColumns = attributeColumns,
        };

    private static List<string> ResolveGroupByColumns(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query)
    {
        if (query.GroupByFields is not { IsDefaultOrEmpty: false } groupBy)
        {
            return [];
        }

        var scope = CreateFieldScope(mapping, attributeColumns);
        var columns = new List<string>(groupBy.Length);
        foreach (var field in groupBy)
        {
            _ = SnowflakeIdentifier.QuoteAttribute(field);
            columns.Add(RelationalFeatureStoreHelpers.ResolveDeclaredField(scope, field, "GROUP BY"));
        }

        return columns;
    }

    private static string BuildStatisticColumns(
        SnowflakeLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        ImmutableArray<StatisticDefinition> statistics)
    {
        var scope = CreateFieldScope(mapping, attributeColumns);
        var columns = new List<string>(statistics.Length);
        foreach (var stat in statistics)
        {
            _ = SnowflakeIdentifier.QuoteAttribute(stat.OnStatisticField);
            var resolved = RelationalFeatureStoreHelpers.ResolveDeclaredField(scope, stat.OnStatisticField, "OutStatistics");
            var fieldExpr = SnowflakeIdentifier.QuoteAttribute(resolved);
            var alias = SnowflakeIdentifier.QuoteAttribute(stat.OutStatisticFieldName);
            columns.Add($"{StatisticExpression(stat.StatisticType, fieldExpr)} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    private static void AppendStatisticsHaving(SnowflakeQueryContext context)
        => RelationalFeatureStoreHelpers.AppendStatisticsHaving(new StatisticsHavingRequest
        {
            Sql = context.Sql,
            FieldScope = context.CreateFieldScope(),
            Query = context.Query,
            QuoteAttribute = SnowflakeIdentifier.QuoteAttribute,
            AddParameter = context.AddParameter,
            FormatStatistic = StatisticExpression,
            FormatOperator = HavingOperator,
        });

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

    private static void AppendWhereClause(SnowflakeQueryContext context)
    {
        if (context.Query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "Snowflake provider does not support EnforcedSqlFilter. Layers with server-enforced " +
                "definition or security filters cannot be served by this provider in this slice.");
        }

        if (context.Query.TextSearch is { } search)
        {
            AppendTextSearchPredicate(context, search);
        }

        AppendWhereOrRejectSqlFilter(context);
    }

    private static void AppendTextSearchPredicate(SnowflakeQueryContext context, FeatureTextSearch search)
    {
        var scope = context.CreateFieldScope();
        var predicate = FeatureTextSearchSql.Build(
            search,
            field =>
            {
                _ = SnowflakeIdentifier.QuoteAttribute(field);
                var resolved = RelationalFeatureStoreHelpers.ResolveDeclaredField(scope, field, "TextSearch");
                return SnowflakeIdentifier.QuoteAttribute(resolved);
            },
            context.AddParameter,
            (column, value) => $"POSITION({value}, {column})");
        context.Sql.Append(" AND ").Append(predicate);
    }

    private static void AppendWhereOrRejectSqlFilter(SnowflakeQueryContext context)
    {
        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // Postgres translator, which emits fragments that are not valid Snowflake SQL.
        if (context.Query.SqlFilterSupplementsWhere)
        {
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the Snowflake provider. " +
                "SqlFilter supplements Where, and the shared ISqlFilterTranslator pipeline emits Postgres-flavored " +
                "SQL which is not valid Snowflake SQL. The supplemental predicates would be dropped if Where " +
                "were re-parsed alone.");
        }

        if (!string.IsNullOrWhiteSpace(context.Query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(context.Query.Where!.Trim(), context);
            context.Sql.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (context.Query.SqlFilter is not null)
        {
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

    private static void AppendOrderByClause(SnowflakeQueryContext context)
        => RelationalFeatureStoreHelpers.AppendDeclaredOrderBy(new DeclaredOrderByRequest
        {
            Sql = context.Sql,
            FieldScope = context.CreateFieldScope(),
            Query = context.Query,
            QuoteAttribute = SnowflakeIdentifier.QuoteAttribute,
        });

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

    private static string ParseAndParameterizeWhereClause(string whereClause, SnowflakeQueryContext context)
        => RelationalFeatureStoreHelpers.RenderWarehouseWhereClause(
            whereClause,
            new WarehouseWhereClauseRequest
            {
                FieldScope = context.CreateFieldScope(),
                QuoteAttribute = SnowflakeIdentifier.QuoteAttribute,
                AddParameter = context.AddParameter,
            });
}
