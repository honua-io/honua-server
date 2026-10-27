// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Translates a <see cref="FeatureQuery"/> into parameterized Snowflake SQL for the Snowflake
/// spatial provider. All identifiers are validated and double-quoted; all literal values flow
/// through SQL parameters (Snowflake positional <c>?</c> binds).
/// </summary>
/// <remarks>
/// Geometry is emitted as OGC 2D WKB via <c>ST_ASWKB(...)</c>. Filter geometries are constructed
/// in-database from WKB with <c>TO_GEOGRAPHY(?, true)</c> / <c>TO_GEOMETRY(?, true)</c> (the second
/// argument enables WKB byte-string parsing). Spatial predicates use Snowflake's <c>ST_*</c>
/// relationship functions.
/// </remarks>
internal static partial class SnowflakeFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT that returns the primary key, the geometry as WKB (or NULL when no
    /// geometry column is configured), and any requested attribute columns.
    /// </summary>
    public static ParameterizedQuery BuildSelectQuery(SnowflakeLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");

        var geometryExpr = BuildGeometryWkbExpression(mapping);
        sb.Append(", ").Append(geometryExpr).Append(" AS \"__geometry\"");

        AppendAttributeColumns(sb, query, attributeColumns);

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, query);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT COUNT(*) for the same query envelope as <see cref="BuildSelectQuery"/>.
    /// </summary>
    public static ParameterizedQuery BuildCountQuery(SnowflakeLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT COUNT(*) FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT that returns the primary key for matching rows. Used for object-id
    /// listings when only identifiers are needed.
    /// </summary>
    public static ParameterizedQuery BuildObjectIdsQuery(SnowflakeLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, query);
        AppendPagination(sb, mapping, query);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds an extent query. Snowflake has no single-call envelope aggregate that returns
    /// corner ordinates, so the builder aggregates each geometry's bounding ordinates with
    /// <c>ST_XMIN</c>/<c>ST_YMIN</c>/<c>ST_XMAX</c>/<c>ST_YMAX</c> over the filtered subset.
    /// </summary>
    public static ParameterizedQuery BuildExtentQuery(SnowflakeLayerMapping mapping, FeatureQuery? query)
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
        AppendWhereClause(sb, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds an aggregate statistics query (<c>outStatistics</c> plus optional
    /// <c>groupByFieldsForStatistics</c>) over the same WHERE, object-id, and spatial
    /// filter envelope as <see cref="BuildSelectQuery"/>.
    /// </summary>
    public static ParameterizedQuery BuildStatistics(SnowflakeLayerMapping mapping, FeatureQuery query)
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
        var groupByColumns = QuoteGroupByColumns(query);

        sb.Append("SELECT ");
        if (groupByColumns.Count > 0)
        {
            sb.Append(string.Join(", ", groupByColumns)).Append(", ");
        }

        sb.Append(BuildStatisticColumns(statistics));
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);

        if (groupByColumns.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", groupByColumns));
        }

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static List<string> QuoteGroupByColumns(FeatureQuery query)
    {
        if (query.GroupByFields is not { IsDefaultOrEmpty: false } groupBy)
        {
            return [];
        }

        var columns = new List<string>(groupBy.Length);
        foreach (var field in groupBy)
        {
            columns.Add(SnowflakeIdentifier.Quote(field));
        }

        return columns;
    }

    private static string BuildStatisticColumns(ImmutableArray<StatisticDefinition> statistics)
    {
        var columns = new List<string>(statistics.Length);
        foreach (var stat in statistics)
        {
            var fieldExpr = SnowflakeIdentifier.Quote(stat.OnStatisticField);
            var alias = SnowflakeIdentifier.Quote(stat.OutStatisticFieldName);
            // STDDEV_SAMP / VAR_SAMP are the sample aggregates named by StatisticType.
            var statExpr = stat.StatisticType switch
            {
                StatisticType.Count => $"COUNT({fieldExpr})",
                StatisticType.Sum => $"SUM({fieldExpr})",
                StatisticType.Min => $"MIN({fieldExpr})",
                StatisticType.Max => $"MAX({fieldExpr})",
                StatisticType.Avg => $"AVG({fieldExpr})",
                StatisticType.Stddev => $"STDDEV_SAMP({fieldExpr})",
                StatisticType.Var => $"VAR_SAMP({fieldExpr})",
                _ => throw new NotSupportedException(
                    $"Statistic type '{stat.StatisticType}' is not supported by the Snowflake provider."),
            };
            columns.Add($"{statExpr} AS {alias}");
        }

        return string.Join(", ", columns);
    }

    /// <summary>
    /// Rejects a <see cref="FeatureQuery.TemporalFilter"/> the Snowflake provider cannot translate.
    /// Temporal filters arrive on <see cref="FeatureQuery"/> from OGC API Features (<c>datetime</c>),
    /// STAC search, and OData time-window queries. The provider does not yet emit Snowflake temporal
    /// predicates, so the filter is surfaced as an eager <see cref="NotSupportedException"/> —
    /// never silently dropped, which would return rows outside the requested window (a
    /// correctness/data-leak bug). Mirrors the fail-loud contract of the MySQL/Redshift/Databricks
    /// providers (temporal GA hardening, #2429).
    /// </summary>
    private static void GuardUnsupportedTemporalFilter(FeatureQuery query)
    {
        if (query.TemporalFilter.HasValue)
        {
            throw new NotSupportedException(
                "Temporal filters are not supported by the Snowflake provider in this slice. " +
                "Apply temporal filtering in the calling layer or use a PostGIS-backed layer.");
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
            SnowflakeIdentifier.EnsureValid(column, "attribute column");
            sb.Append(", ").Append(SnowflakeIdentifier.Quote(column));
        }
    }

    private static void AppendWhereClause(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        if (query.TextSearch is { } search)
        {
            var predicate = FeatureTextSearchSql.Build(search, SnowflakeIdentifier.Quote, text =>
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
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), parameters);
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
            // filter geometry to match the canonical PostGIS reference (#2068). Reversing the
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

        parameters.Add(filter.Geometry);

        // TO_GEOGRAPHY/TO_GEOMETRY parse a WKB byte string when the second argument is true.
        return $"{mapping.SpatialConstructor}(?, TRUE)";
    }

    private static void AppendOrderByClause(StringBuilder sb, FeatureQuery query)
    {
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var clauses = new List<string>(query.OrderBy.Value.Length);
        foreach (var orderBy in query.OrderBy.Value)
        {
            SnowflakeIdentifier.EnsureValid(orderBy.Field, "order-by column");
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{SnowflakeIdentifier.Quote(orderBy.Field)} {direction}");
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

    private static string ParseAndParameterizeWhereClause(string whereClause, List<object> parameters)
    {
        var expressions = WhereClauseSplitter.SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("WHERE clause format not supported.");
        }

        var rendered = new List<string>(expressions.Count);
        // Not rewritten as .Select(): each iteration is a multi-branch parser that can
        // throw, mutate the shared `parameters` accumulator, and `continue` early per
        // branch -- not a pure map of one iteration variable to another.
        foreach (var trimmed in (expressions).Select(raw => raw.Trim()))
        {
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("WHERE clause format not supported.");
            }

            if (trimmed.Equals("1=1", StringComparison.Ordinal) ||
                trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                rendered.Add("1=1");
                continue;
            }

            var nullMatch = NullCheckRegex().Match(trimmed);
            if (nullMatch.Success)
            {
                var field = nullMatch.Groups["field"].Value;
                SnowflakeIdentifier.EnsureValid(field, "WHERE column");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{SnowflakeIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var inMatch = InExpressionRegex().Match(trimmed);
            if (inMatch.Success)
            {
                var field = inMatch.Groups["field"].Value;
                SnowflakeIdentifier.EnsureValid(field, "WHERE column");
                var placeholders = new List<string>();
                foreach (Match valueMatch in InValueRegex().Matches(inMatch.Groups["values"].Value))
                {
                    placeholders.Add("?");
                    parameters.Add(ParseValueToken(valueMatch.Value));
                }
                rendered.Add($"{SnowflakeIdentifier.Quote(field)} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            var compMatch = ComparisonRegex().Match(trimmed);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                SnowflakeIdentifier.EnsureValid(field, "WHERE column");
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var value = ParseValueToken(compMatch.Groups["value"].Value);

                parameters.Add(value);
                rendered.Add($"{SnowflakeIdentifier.Quote(field)} {op} ?");
                continue;
            }

            throw new ArgumentException("WHERE clause format not supported.");
        }

        return string.Join(" AND ", rendered);
    }


    private static string NormalizeOperator(string op) => op.Trim().ToUpperInvariant() switch
    {
        "NOT LIKE" => "NOT LIKE",
        "LIKE" => "LIKE",
        ">=" => ">=",
        "<=" => "<=",
        "!=" or "<>" => "<>",
        "=" => "=",
        ">" => ">",
        "<" => "<",
        _ => throw new ArgumentException($"Unsupported operator: {op}")
    };

    private static object ParseValueToken(string valueToken)
    {
        if (valueToken.StartsWith('\'') && valueToken.EndsWith('\''))
        {
            return valueToken[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return num;
        }

        // Defense-in-depth: ComparisonRegex constrains the 'value' group to a quoted string or
        // numeric literal, so this branch is currently unreachable. Throw explicitly so any
        // future code path that calls this function with an unexpected token fails loudly rather
        // than silently returning a raw string that could produce unparameterized SQL.
        throw new ArgumentException($"Unrecognized value token: {valueToken}");
    }

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    [GeneratedRegex(@"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InExpressionRegex();

    [GeneratedRegex(@"'(?:''|[^'])*'|-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex InValueRegex();
}
