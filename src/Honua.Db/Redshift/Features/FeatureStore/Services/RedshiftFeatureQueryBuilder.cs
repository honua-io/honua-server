// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Redshift.Features.FeatureStore.Services;

internal static partial class RedshiftFeatureQueryBuilder
{
    public static ParameterizedQuery BuildSelectQuery(RedshiftLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);
        GuardUnsupportedFilters(query);

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
        AppendPagination(sb, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildCountQuery(
        RedshiftLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedFilters(query);

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
        RedshiftLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedFilters(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, mapping, attributeColumns, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, mapping, attributeColumns, query);
        AppendPagination(sb, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildExtentQuery(
        RedshiftLayerMapping mapping,
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
        GuardUnsupportedFilters(effective);

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var geometryExpr = BuildExtentGeometryExpression(mapping);

        sb.Append("SELECT ");
        sb.Append("MIN(ST_XMin(").Append(geometryExpr).Append(")) AS \"min_x\", ");
        sb.Append("MIN(ST_YMin(").Append(geometryExpr).Append(")) AS \"min_y\", ");
        sb.Append("MAX(ST_XMax(").Append(geometryExpr).Append(")) AS \"max_x\", ");
        sb.Append("MAX(ST_YMax(").Append(geometryExpr).Append(")) AS \"max_y\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(mapping.QuotedGeometryColumn!).Append(" IS NOT NULL");

        AppendWhereClause(sb, mapping, attributeColumns, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    public static ParameterizedQuery BuildStatistics(
        RedshiftLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedFilters(query);

        if (query.OutStatistics is not { IsDefaultOrEmpty: false } statistics)
        {
            // No aggregates requested: return an empty result rather than a malformed SELECT.
            return new ParameterizedQuery("SELECT 1 WHERE FALSE", new List<object>());
        }

        var sb = new StringBuilder();
        var parameters = new List<object>();
        var resolvedGroupByColumns = ResolveGroupByColumns(mapping, attributeColumns, query);
        var groupByColumns = resolvedGroupByColumns.Select(RedshiftIdentifier.QuoteAttribute).ToList();

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
        RelationalFeatureStoreHelpers.AppendStatisticsOrderBy(
            sb,
            statistics,
            resolvedGroupByColumns,
            query,
            RedshiftIdentifier.QuoteAttribute);
        // Same LIMIT / OFFSET binding as SELECT. FeatureServer sends MaxRecordCount+1 so the
        // warehouse, not the server, bounds the grouped row set.
        AppendPagination(sb, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static List<string> ResolveGroupByColumns(
        RedshiftLayerMapping mapping,
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
            _ = RedshiftIdentifier.QuoteAttribute(field);
            columns.Add(ResolveDeclaredField(mapping, attributeColumns, field, "GROUP BY"));
        }

        return columns;
    }

    private static string BuildStatisticColumns(
        RedshiftLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        ImmutableArray<StatisticDefinition> statistics)
    {
        var columns = new List<string>(statistics.Length);
        foreach (var stat in statistics)
        {
            _ = RedshiftIdentifier.QuoteAttribute(stat.OnStatisticField);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, stat.OnStatisticField, "OutStatistics");
            var fieldExpr = RedshiftIdentifier.QuoteAttribute(resolved);
            var alias = RedshiftIdentifier.QuoteAttribute(stat.OutStatisticFieldName);
            columns.Add($"{StatisticExpression(stat.StatisticType, fieldExpr)} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    private static void AppendStatisticsHaving(
        StringBuilder sb,
        RedshiftLayerMapping mapping,
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
            _ = RedshiftIdentifier.QuoteAttribute(condition.OnStatisticField);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, condition.OnStatisticField, "HAVING");
            var fieldExpr = RedshiftIdentifier.QuoteAttribute(resolved);
            var marker = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(condition.BoundValue());
            sb.Append(StatisticExpression(condition.StatisticType, fieldExpr))
                .Append(' ')
                .Append(HavingOperator(condition.Operator))
                .Append(' ')
                .Append(marker);
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
                $"Statistic type '{statisticType}' is not supported by the Redshift provider."),
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
                $"Having operator '{op}' is not supported by the Redshift provider."),
        };

    private static string BuildGeometryWkbExpression(RedshiftLayerMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return "CAST(NULL AS VARBYTE)";
        }

        // ST_AsBinary emits OGC 2D WKB. Geography columns are cast to geometry first because
        // ST_AsBinary is defined on the geometry type in Redshift.
        return mapping.GeometryColumnType == RedshiftGeometryColumnType.Geography
            ? $"ST_AsBinary(ST_GeomFromWKB(ST_AsBinary({mapping.QuotedGeometryColumn})))"
            : $"ST_AsBinary({mapping.QuotedGeometryColumn})";
    }

    private static string BuildExtentGeometryExpression(RedshiftLayerMapping mapping)
    {
        // Redshift's bounding-box accessors (ST_XMin, …) operate on geometry. Geography layers are
        // round-tripped through ST_AsBinary/ST_GeomFromWKB to obtain a geometry value.
        return mapping.GeometryColumnType == RedshiftGeometryColumnType.Geography
            ? $"ST_GeomFromWKB(ST_AsBinary({mapping.QuotedGeometryColumn!}))"
            : mapping.QuotedGeometryColumn!;
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
            sb.Append(", ").Append(RedshiftIdentifier.QuoteAttribute(column));
        }
    }

    private static void AppendWhereClause(
        StringBuilder sb,
        RedshiftLayerMapping mapping,
        IReadOnlyList<string>? attributeColumns,
        FeatureQuery query,
        List<object> parameters)
    {
        if (query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "Redshift provider does not support EnforcedSqlFilter. Layers with server-enforced " +
                "definition or security filters cannot be served by this provider in this slice.");
        }

        if (query.TextSearch is { } search)
        {
            var predicate = FeatureTextSearchSql.Build(search, field =>
            {
                _ = RedshiftIdentifier.QuoteAttribute(field);
                var resolved = ResolveDeclaredField(mapping, attributeColumns, field, "TextSearch");
                return RedshiftIdentifier.QuoteAttribute(resolved);
            }, text =>
            {
                var marker = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(text);
                return marker;
            }, (column, value) => $"STRPOS({column}, {value})");
            sb.Append(" AND ").Append(predicate);
        }

        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres (PostGIS) translator
        // today. PostGIS-emitted fragments use JSONB operators, ::casts, and PostGIS-only spatial
        // functions that Redshift does not implement, so we re-parse Where here using this
        // provider's own parser whenever it is supplied, and reject translated SqlFilter fragments.
        // A supplemental fragment (time filter or object ids that were not forwarded) is not
        // represented by Where; executing Where alone would drop those predicates.
        if (query.SqlFilterSupplementsWhere)
        {
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the Redshift provider. " +
                "SqlFilter supplements Where, and the shared ISqlFilterTranslator pipeline emits PostGIS-flavored " +
                "SQL which Redshift's spatial layer does not implement. The supplemental predicates would be " +
                "dropped if Where were re-parsed alone.");
        }

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), mapping, attributeColumns, parameters);
            sb.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (query.SqlFilter is not null)
        {
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the Redshift provider. " +
                "The shared ISqlFilterTranslator pipeline emits PostGIS-flavored SQL which Redshift's spatial " +
                "layer does not implement. Route the request through a protocol path that populates the canonical " +
                "Where text (FeatureServer 'where'), or restrict CQL2/FES/OData $filter usage to providers that " +
                "register their own ISqlFilterTranslator.");
        }
    }

    private static void AppendObjectIdsFilter(StringBuilder sb, RedshiftLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.ObjectIds.HasValue || query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var ids = query.ObjectIds.Value;
        if (ids.Length > 30_000)
        {
            throw new NotSupportedException(
                $"The object id list of {ids.Length.ToString(CultureInfo.InvariantCulture)} values exceeds this provider's parameter limit of 30000.");
        }

        var placeholders = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders[i] = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(ids[i]);
        }

        sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn).Append(" IN (").Append(string.Join(", ", placeholders)).Append(')');
    }

    private static void AppendSpatialFilter(StringBuilder sb, RedshiftLayerMapping mapping, FeatureQuery query, List<object> parameters)
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

        if (filter.SpatialRelationship is SpatialRelationship.NearestNeighbor
            or SpatialRelationship.WithinDistance
            or SpatialRelationship.BeyondDistance)
        {
            throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' (distance/nearest-neighbor) is not supported by the Redshift provider in this slice.");
        }

        var geomCol = BuildExtentGeometryExpression(mapping);
        var filterExpr = BuildFilterGeometryExpression(filter, mapping, parameters);

        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects =>
                $"ST_Intersects({geomCol}, {filterExpr})",
            SpatialRelationship.EnvelopeIntersects =>
                $"ST_Intersects(ST_Envelope({geomCol}), ST_Envelope({filterExpr}))",
            // Esri semantics: esriSpatialRelWithin = filter geometry is within feature geometry;
            // esriSpatialRelContains = filter geometry contains feature geometry. Lead with the
            // filter geometry to match the canonical PostGIS reference. Reversing the
            // operands inverts the relationship (ST_Within(A,B) == ST_Contains(B,A)) and returns
            // the wrong (typically empty) result set.
            SpatialRelationship.Within =>
                $"ST_Within({filterExpr}, {geomCol})",
            SpatialRelationship.Contains =>
                $"ST_Contains({filterExpr}, {geomCol})",
            SpatialRelationship.Disjoint =>
                $"ST_Disjoint({geomCol}, {filterExpr})",
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the Redshift provider in this slice.")
        };

        sb.Append(" AND ").Append(clause);
    }

    private static string BuildFilterGeometryExpression(SpatialFilter filter, RedshiftLayerMapping mapping, List<object> parameters)
    {
        // Redshift spatial predicates require both operands share the same SRID; otherwise the
        // function raises an error. Mirror the SQL Server / MySQL providers and reject cross-SRID
        // filters up front with a clear, actionable error instead of a runtime failure.
        if (filter.Srid is > 0 && mapping.Srid.HasValue && filter.Srid.Value != mapping.Srid.Value)
        {
            throw new NotSupportedException(
                $"Cross-SRID spatial filter is not supported by the Redshift provider: " +
                $"filter SRID {filter.Srid.Value} differs from layer SRID {mapping.Srid.Value}. " +
                $"Pre-project the filter geometry to SRID {mapping.Srid.Value} before submitting the request.");
        }

        var wkbParam = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(WkbSridNormalizer.RemoveEmbeddedSrid(filter.Geometry));

        var srid = filter.Srid is > 0 ? filter.Srid.Value : mapping.Srid ?? 0;
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);

        // ST_GeomFromWKB(wkb, srid) parses OGC WKB and tags the requested SRID. For geography
        // layers the parsed geometry is compared against the column after the column is converted
        // to geometry in AppendSpatialFilter.
        return $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
    }

    private static void AppendOrderByClause(
        StringBuilder sb,
        RedshiftLayerMapping mapping,
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
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            _ = RedshiftIdentifier.QuoteAttribute(orderBy.Field);
            var resolved = ResolveDeclaredField(mapping, attributeColumns, orderBy.Field, "ORDER BY");
            clauses.Add($"{RedshiftIdentifier.QuoteAttribute(resolved)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static void AppendPagination(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        if (!query.Limit.HasValue && (!query.Offset.HasValue || query.Offset.Value <= 0))
        {
            return;
        }

        // Redshift uses standard PostgreSQL LIMIT / OFFSET syntax. Unlike SQL Server, ORDER BY is
        // not required for OFFSET, but callers that paginate without an ORDER BY accept the same
        // undefined-order caveat documented on the other providers.
        if (query.Limit.HasValue)
        {
            sb.Append(" LIMIT @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
            parameters.Add(query.Limit.Value);
        }

        var offset = query.Offset.GetValueOrDefault(0);
        if (offset > 0)
        {
            sb.Append(" OFFSET @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
            parameters.Add(offset);
        }
    }

    private static void GuardUnsupportedFilters(FeatureQuery query)
    {
        if (query.TemporalFilter.HasValue)
        {
            throw new NotSupportedException(
                "Temporal filters are not supported by the Redshift provider in this slice.");
        }

        if (query.OutputSrid.HasValue)
        {
            throw new NotSupportedException(
                "Output SRID reprojection is not supported by the Redshift provider in this slice; " +
                "request features in the layer's storage SRID.");
        }
    }

    private static string ParseAndParameterizeWhereClause(
        string whereClause,
        RedshiftLayerMapping mapping,
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
                _ = RedshiftIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{RedshiftIdentifier.QuoteAttribute(field)} IS {notClause}NULL");
                continue;
            }

            var inMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.In)
            {
                var field = inMatch.Groups["field"].Value;
                _ = RedshiftIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var placeholders = new List<string>();
                foreach (Match valueMatch in LegacyWhereClauseParser.MatchInValues(inMatch.Groups["values"].Value))
                {
                    placeholders.Add("@p" + parameters.Count.ToString(CultureInfo.InvariantCulture));
                    parameters.Add(LegacyWhereClauseParser.ParseValueToken(valueMatch.Value, LegacyWhereValuePolicy.NumericRequired));
                }
                rendered.Add($"{RedshiftIdentifier.QuoteAttribute(field)} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            var compMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.Comparison)
            {
                var field = compMatch.Groups["field"].Value;
                _ = RedshiftIdentifier.QuoteAttribute(field);
                field = ResolveDeclaredField(mapping, attributeColumns, field, "WHERE");
                var op = LegacyWhereClauseParser.NormalizeOperator(compMatch.Groups["op"].Value, LegacyWhereGrammar.Warehouse);
                var value = LegacyWhereClauseParser.ParseValueToken(compMatch.Groups["value"].Value, LegacyWhereValuePolicy.NumericRequired);

                var paramName = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(value);
                rendered.Add($"{RedshiftIdentifier.QuoteAttribute(field)} {op} {paramName}");
                continue;
            }

            throw new ArgumentException("WHERE clause format not supported.");
        }

        return string.Join(" AND ", rendered);
    }

    private static string ResolveDeclaredField(
        RedshiftLayerMapping mapping,
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
