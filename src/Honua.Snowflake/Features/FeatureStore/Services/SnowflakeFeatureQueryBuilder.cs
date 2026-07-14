// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

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

        IEnumerable<string> columns = attributeColumns;
        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
        {
            var requested = new HashSet<string>(query.OutFields.Value, StringComparer.OrdinalIgnoreCase);
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
        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres translator today.
        // Passing a Postgres-emitted fragment through here would yield invalid Snowflake SQL, so
        // we re-parse Where using this provider's own Snowflake-aware parser whenever it is supplied.
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
        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("WHERE clause format not supported.");
        }

        var rendered = new List<string>(expressions.Count);
        // Not rewritten as .Select(): each iteration is a multi-branch parser that can
        // throw, mutate the shared `parameters` accumulator, and `continue` early per
        // branch -- not a pure map of one iteration variable to another.
        foreach (var raw in expressions)
        {
            var trimmed = raw.Trim();
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
                     (i == 0 || !IsIdentifierChar(whereClause[i - 1])) &&
                     (i + 3 >= whereClause.Length || !IsIdentifierChar(whereClause[i + 3])))
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

    // Identifier-boundary helper used by SplitOnAnd. Mirrors the regex character class
    // [A-Za-z0-9_] so column names that contain or begin with "and" (e.g. start_and_end,
    // and_flag) are not falsely split as logical AND boundaries.
    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

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
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();
}
