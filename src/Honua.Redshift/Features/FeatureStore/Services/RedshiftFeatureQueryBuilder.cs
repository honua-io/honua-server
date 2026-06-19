// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Translates a <see cref="FeatureQuery"/> into parameterized Redshift SQL for the read-only
/// Redshift spatial provider. All identifiers are validated and double-quoted; all literal
/// values flow through Npgsql named parameters.
/// </summary>
/// <remarks>
/// Redshift speaks the PostgreSQL wire protocol but its spatial layer is <b>not</b> PostGIS.
/// The builder therefore restricts itself to Redshift-native spatial SQL functions
/// (<c>ST_AsBinary</c>, <c>ST_GeomFromWKB</c>, <c>ST_Intersects</c>, <c>ST_Within</c>,
/// <c>ST_Contains</c>, <c>ST_Disjoint</c>, <c>ST_XMin</c>/<c>ST_YMin</c>/<c>ST_XMax</c>/<c>ST_YMax</c>)
/// and the native <c>GEOMETRY</c>/<c>GEOGRAPHY</c> types.
/// </remarks>
internal static partial class RedshiftFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT that returns the primary key, the geometry as WKB (or NULL when no
    /// geometry column is configured), and any requested attribute columns.
    /// </summary>
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

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, mapping, query);
        AppendPagination(sb, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT COUNT(*) for the same query envelope as <see cref="BuildSelectQuery"/>.
    /// </summary>
    public static ParameterizedQuery BuildCountQuery(RedshiftLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedFilters(query);

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
    public static ParameterizedQuery BuildObjectIdsQuery(RedshiftLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedFilters(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        AppendOrderByClause(sb, mapping, query);
        AppendPagination(sb, query, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds an extent query using Redshift's native <c>ST_XMin</c>/<c>ST_YMin</c>/<c>ST_XMax</c>/
    /// <c>ST_YMax</c> accessors aggregated with <c>MIN</c>/<c>MAX</c>. Geography columns are cast to
    /// geometry first so the planar bounding-box accessors apply.
    /// </summary>
    public static ParameterizedQuery BuildExtentQuery(RedshiftLayerMapping mapping, FeatureQuery? query)
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

        AppendWhereClause(sb, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

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

        IEnumerable<string> columns = attributeColumns;
        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
        {
            var requested = new HashSet<string>(query.OutFields.Value, StringComparer.OrdinalIgnoreCase);
            columns = attributeColumns.Where(c => requested.Contains(c));
        }

        foreach (var column in columns)
        {
            RedshiftIdentifier.EnsureValid(column, "attribute column");
            sb.Append(", ").Append(RedshiftIdentifier.Quote(column));
        }
    }

    private static void AppendWhereClause(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres (PostGIS) translator
        // today. PostGIS-emitted fragments use JSONB operators, ::casts, and PostGIS-only spatial
        // functions that Redshift does not implement, so we re-parse Where here using this
        // provider's own parser whenever it is supplied, and reject translated SqlFilter fragments.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), parameters);
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

        var geomCol = mapping.QuotedGeometryColumn!;
        var filterExpr = BuildFilterGeometryExpression(filter, mapping, parameters);

        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects =>
                $"ST_Intersects({geomCol}, {filterExpr})",
            SpatialRelationship.EnvelopeIntersects =>
                $"ST_Intersects(ST_Envelope({geomCol}), ST_Envelope({filterExpr}))",
            SpatialRelationship.Within =>
                $"ST_Within({geomCol}, {filterExpr})",
            SpatialRelationship.Contains =>
                $"ST_Contains({geomCol}, {filterExpr})",
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
        parameters.Add(filter.Geometry);

        var srid = filter.Srid is > 0 ? filter.Srid.Value : mapping.Srid ?? 0;
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);

        // ST_GeomFromWKB(wkb, srid) parses OGC WKB and tags the requested SRID. For geography
        // layers the parsed geometry is compared against the column after the column is cast to
        // geometry by the predicate's accessor in BuildExtent/spatial paths.
        return $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
    }

    private static void AppendOrderByClause(StringBuilder sb, RedshiftLayerMapping mapping, FeatureQuery query)
    {
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var clauses = new List<string>(query.OrderBy.Value.Length);
        foreach (var orderBy in query.OrderBy.Value)
        {
            RedshiftIdentifier.EnsureValid(orderBy.Field, "order-by column");
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{RedshiftIdentifier.Quote(orderBy.Field)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
        _ = mapping;
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
                RedshiftIdentifier.EnsureValid(field, "WHERE column");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{RedshiftIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var compMatch = ComparisonRegex().Match(trimmed);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                RedshiftIdentifier.EnsureValid(field, "WHERE column");
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var value = ParseValueToken(compMatch.Groups["value"].Value);

                var paramName = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(value);
                rendered.Add($"{RedshiftIdentifier.Quote(field)} {op} {paramName}");
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
    // [a-zA-Z0-9_] so column names that contain or begin with "and" (e.g. start_and_end,
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
}
