// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Translates a <see cref="FeatureQuery"/> into parameterized T-SQL for the SQL Server
/// spatial provider. All identifiers are validated and bracket-quoted; all literal values
/// flow through SQL parameters.
/// </summary>
internal static partial class SqlServerFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT that returns the primary key, the geometry as WKB (or NULL when no
    /// geometry column is configured), and any requested attribute columns.
    /// </summary>
    public static ParameterizedQuery BuildSelectQuery(SqlServerLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS [__objectid]");

        var geometryExpr = BuildGeometryWkbExpression(mapping);
        sb.Append(", ").Append(geometryExpr).Append(" AS [__geometry]");

        AppendAttributeColumns(sb, query, attributeColumns);

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, query);
        AppendPagination(sb, mapping, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT COUNT(*) for the same query envelope as <see cref="BuildSelectQuery"/>.
    /// </summary>
    public static ParameterizedQuery BuildCountQuery(SqlServerLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT COUNT_BIG(*) FROM ").Append(mapping.QuotedTableReference);
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
    public static ParameterizedQuery BuildObjectIdsQuery(SqlServerLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS [__objectid]");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, query);
        AppendPagination(sb, mapping, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds an extent query using <c>geometry::EnvelopeAggregate</c> or
    /// <c>geography::EnvelopeAggregate</c> (both SQL Server 2012+). The corner extraction
    /// uses geometry-typed <c>STX</c>/<c>STY</c> properties for planar layers and
    /// geography-typed <c>Long</c>/<c>Lat</c> properties for geodetic layers, since SQL Server
    /// exposes a different point coordinate API per spatial type.
    /// </summary>
    public static ParameterizedQuery BuildExtentQuery(SqlServerLayerMapping mapping, FeatureQuery? query)
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
        var isGeography = mapping.GeometryColumnType == SqlServerGeometryColumnType.Geography;
        var xProperty = isGeography ? "Long" : "STX";
        var yProperty = isGeography ? "Lat" : "STY";

        // EnvelopeAggregate returns a 5-point rectangle ring for an area extent: STPointN(1) is the
        // lower-left corner and STPointN(3) the upper-right. For a degenerate aggregate (a layer whose
        // only geometry is a single point, or whose geometries are all collinear) the result collapses
        // to a Point/LineString and STPointN(3) is NULL. The data-access layer treats a NULL max corner
        // as "no extent" (see SqlServerFeatureDataAccess.ExecuteExtentAsync), so such a layer reports no
        // extent rather than a zero-area extent. That edge case is accepted here so the emitted SQL
        // stays a plain corner projection; callers needing a point layer's extent should derive it from
        // the feature geometry directly.
        sb.Append("SELECT ");
        sb.Append("envelope.STPointN(1).").Append(xProperty).Append(" AS [min_x], ");
        sb.Append("envelope.STPointN(1).").Append(yProperty).Append(" AS [min_y], ");
        sb.Append("envelope.STPointN(3).").Append(xProperty).Append(" AS [max_x], ");
        sb.Append("envelope.STPointN(3).").Append(yProperty).Append(" AS [max_y] ");
        sb.Append("FROM (SELECT ").Append(mapping.GeometryTypeToken).Append("::EnvelopeAggregate(").Append(geometryColumn).Append(") AS envelope FROM ");
        sb.Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geometryColumn).Append(" IS NOT NULL");

        var effective = query ?? new FeatureQuery();
        AppendWhereClause(sb, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        sb.Append(") AS aggregated");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static string BuildGeometryWkbExpression(SqlServerLayerMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return "CAST(NULL AS varbinary(max))";
        }

        // Both geometry and geography expose STAsBinary for OGC WKB output.
        return $"{mapping.QuotedGeometryColumn}.STAsBinary()";
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
            SqlServerIdentifier.EnsureValid(column, "attribute column");
            sb.Append(", ").Append(SqlServerIdentifier.Quote(column));
        }
    }

    private static void AppendWhereClause(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        // The provider-enforced filter (a layer's permanent / row-restricting predicate) is applied
        // first, ahead of every caller-supplied filter, so it can never be widened by the caller. It
        // is a programmatically built SqlFragment that already uses @p0, @p1, ... placeholders.
        if (query.EnforcedSqlFilter is { } enforcedFilter)
        {
            AppendSqlFragment(sb, enforcedFilter, parameters);
        }

        // Caller-supplied filter precedence on this provider intentionally diverges from the generic
        // "SqlFilter takes precedence over Where" contract, because the only registered
        // ISqlFilterTranslator emits Postgres dialect. FeatureServer always sets the canonical Where
        // text alongside that Postgres-styled SqlFilter, so pasting the fragment into T-SQL would emit
        // invalid SQL (JSON path operators, dollar-sign placeholders). We therefore re-parse Where
        // with this provider's own SQL Server-aware parser whenever it is present, and only consume a
        // SqlFilter directly when no Where accompanies it (the direct-programmatic-caller path, where
        // the fragment is trusted to already be SQL Server-styled with @p0, @p1, ... placeholders).
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), parameters);
            sb.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (query.SqlFilter is { } sqlFilter)
        {
            AppendSqlFragment(sb, sqlFilter, parameters);
        }
    }

    /// <summary>
    /// Appends a programmatically built <see cref="SqlFragment"/> as an ANDed conjunct, re-binding its
    /// <c>@pN</c> placeholders to the command's next available parameter indices so caller and provider
    /// parameters never collide.
    /// </summary>
    private static void AppendSqlFragment(StringBuilder sb, SqlFragment fragment, List<object> parameters)
    {
        var startIndex = parameters.Count;
        var rebound = RebindNamedParameters(fragment.Sql, startIndex, fragment.Parameters.Count);
        sb.Append(" AND (").Append(rebound).Append(')');
        foreach (var param in fragment.Parameters)
        {
            parameters.Add(param ?? DBNull.Value);
        }
    }

    private static void AppendObjectIdsFilter(StringBuilder sb, SqlServerLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.ObjectIds.HasValue || query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var ids = query.ObjectIds.Value;
        var placeholders = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders[i] = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(ids[i]);
        }

        sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn).Append(" IN (").Append(string.Join(", ", placeholders)).Append(')');
    }

    private static void AppendSpatialFilter(StringBuilder sb, SqlServerLayerMapping mapping, FeatureQuery query, List<object> parameters)
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
        var isGeography = mapping.GeometryColumnType == SqlServerGeometryColumnType.Geography;

        // STEnvelope is a geometry-only API; geography has no analogous bounding-box accessor.
        // Reject EnvelopeIntersects on geography rather than emit SQL that fails at execution.
        if (isGeography && filter.SpatialRelationship == SpatialRelationship.EnvelopeIntersects)
        {
            throw new NotSupportedException(
                "EnvelopeIntersects is not supported on SQL Server geography columns; STEnvelope is geometry-only. Use Intersects instead.");
        }

        var filterExpr = BuildFilterGeometryExpression(filter, mapping, parameters);

        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects =>
                $"{geomCol}.STIntersects({filterExpr}) = 1",
            SpatialRelationship.EnvelopeIntersects =>
                $"{geomCol}.STEnvelope().STIntersects({filterExpr}.STEnvelope()) = 1",
            SpatialRelationship.Within =>
                $"{geomCol}.STWithin({filterExpr}) = 1",
            SpatialRelationship.Contains =>
                $"{geomCol}.STContains({filterExpr}) = 1",
            SpatialRelationship.Disjoint =>
                $"{geomCol}.STDisjoint({filterExpr}) = 1",
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the SQL Server provider in this slice.")
        };

        sb.Append(" AND ").Append(clause);
    }

    private static string BuildFilterGeometryExpression(SpatialFilter filter, SqlServerLayerMapping mapping, List<object> parameters)
    {
        var wkbParam = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(filter.Geometry);

        // SQL Server's geometry/geography static parsers require an explicit SRID. Use the
        // filter's SRID when supplied; otherwise inherit the layer SRID for safe comparison.
        var srid = filter.Srid is > 0 ? filter.Srid.Value : mapping.Srid ?? 0;
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);

        return mapping.GeometryColumnType == SqlServerGeometryColumnType.Geography
            ? $"geography::STGeomFromWKB({wkbParam}, {sridLiteral})"
            : $"geometry::STGeomFromWKB({wkbParam}, {sridLiteral})";
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
            SqlServerIdentifier.EnsureValid(orderBy.Field, "order-by column");
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{SqlServerIdentifier.Quote(orderBy.Field)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static void AppendPagination(StringBuilder sb, SqlServerLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.Limit.HasValue && (!query.Offset.HasValue || query.Offset.Value <= 0))
        {
            return;
        }

        // SQL Server requires ORDER BY before OFFSET / FETCH NEXT. When the caller did not supply
        // one, fall back to the primary key so paging is deterministic — ORDER BY (SELECT 1) leaves
        // row order undefined and can skip or duplicate rows across pages.
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            sb.Append(" ORDER BY ").Append(mapping.QuotedPrimaryKeyColumn);
        }

        var offset = query.Offset.GetValueOrDefault(0);
        sb.Append(" OFFSET @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture)).Append(" ROWS");
        parameters.Add(offset);

        if (query.Limit.HasValue)
        {
            sb.Append(" FETCH NEXT @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture)).Append(" ROWS ONLY");
            parameters.Add(query.Limit.Value);
        }
    }

    /// <summary>
    /// Re-binds the <c>@pN</c> placeholders of a caller-supplied SQL fragment onto the command's next
    /// available parameter indices. Each distinct original index maps to a single new index, so a
    /// placeholder reused multiple times in the fragment (e.g. <c>@p0 BETWEEN @p0 AND @p1</c>) keeps
    /// referring to the same value rather than fanning out into extra parameter names that have no bound
    /// value. The original indices must be the contiguous range <c>0..parameterCount-1</c>, matching the
    /// values the caller supplies alongside the fragment.
    /// </summary>
    /// <param name="sql">Fragment SQL using <c>@p0</c>, <c>@p1</c>, ... placeholders.</param>
    /// <param name="startIndex">First free parameter index in the surrounding command.</param>
    /// <param name="parameterCount">Number of values supplied with the fragment.</param>
    private static string RebindNamedParameters(string sql, int startIndex, int parameterCount)
    {
        // The caller appends fragment values in original index order (value i -> command parameter
        // startIndex + i), so each original @pN maps deterministically to @p(startIndex + N). Mapping
        // by original index (rather than allocating a fresh sequential name per occurrence) means a
        // placeholder reused several times keeps referring to the same bound value and the emitted
        // parameter-name count never exceeds the supplied value count.
        return NamedParameterRegex().Replace(sql, match =>
        {
            var original = int.Parse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (original < 0 || original >= parameterCount)
            {
                throw new ArgumentException(
                    $"SQL fragment references parameter @p{original} but only {parameterCount} parameter value(s) were supplied.",
                    nameof(sql));
            }

            return "@p" + (startIndex + original).ToString(CultureInfo.InvariantCulture);
        });
    }

    private static string ParseAndParameterizeWhereClause(string whereClause, List<object> parameters)
    {
        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("WHERE clause format not supported.");
        }

        var rendered = new List<string>(expressions.Count);
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
                SqlServerIdentifier.EnsureValid(field, "WHERE column");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{SqlServerIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var compMatch = ComparisonRegex().Match(trimmed);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                SqlServerIdentifier.EnsureValid(field, "WHERE column");
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var value = ParseValueToken(compMatch.Groups["value"].Value);

                var paramName = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(value);
                rendered.Add($"{SqlServerIdentifier.Quote(field)} {op} {paramName}");
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
                     string.Compare(whereClause, i, "AND", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                     (i == 0 || !char.IsLetterOrDigit(whereClause[i - 1])) &&
                     (i + 3 >= whereClause.Length || !char.IsLetterOrDigit(whereClause[i + 3])))
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

        return valueToken;
    }

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameterRegex();
}
