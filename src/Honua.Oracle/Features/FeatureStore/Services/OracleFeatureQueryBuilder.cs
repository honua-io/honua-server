// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Translates a <see cref="FeatureQuery"/> into parameterized Oracle SQL for the Oracle
/// spatial provider. All identifiers are validated and double-quoted; all literal values
/// flow through named bind parameters (<c>:p0</c>, <c>:p1</c>, …) with
/// <c>BindByName = true</c>.
/// </summary>
/// <remarks>
/// Oracle 12c+ is assumed for <c>OFFSET … FETCH NEXT</c> pagination. Spatial functions used:
/// <list type="bullet">
///   <item><c>SDO_UTIL.TO_WKBGEOMETRY</c> (Oracle Spatial 10g R2+) for OGC WKB output.</item>
///   <item><c>SDO_UTIL.FROM_WKBGEOMETRY</c> (Oracle Spatial 11g R1+) for filter geometries.</item>
///   <item><c>SDO_RELATE</c> + <c>SDO_AGGR_MBR</c> for spatial filtering and extents.</item>
/// </list>
/// </remarks>
internal static partial class OracleFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT that returns the primary key, the geometry as WKB (or NULL when no
    /// geometry column is configured), and any requested attribute columns.
    /// </summary>
    public static ParameterizedQuery BuildSelectQuery(OracleLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");

        sb.Append(", ").Append(BuildGeometryWkbExpression(mapping)).Append(" AS \"__geometry\"");

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
    public static ParameterizedQuery BuildCountQuery(OracleLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);

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
    public static ParameterizedQuery BuildObjectIdsQuery(OracleLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS \"__objectid\"");
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
    /// Builds an extent query using <c>SDO_AGGR_MBR</c>. The aggregate returns a rectangle
    /// SDO_GEOMETRY whose <c>SDO_ORDINATES</c> varray contains <c>(minX, minY, maxX, maxY)</c>
    /// in the source CRS.
    /// </summary>
    public static ParameterizedQuery BuildExtentQuery(OracleLayerMapping mapping, FeatureQuery? query)
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
        sb.Append("t.mbb.SDO_ORDINATES(1) AS \"min_x\", ");
        sb.Append("t.mbb.SDO_ORDINATES(2) AS \"min_y\", ");
        sb.Append("t.mbb.SDO_ORDINATES(3) AS \"max_x\", ");
        sb.Append("t.mbb.SDO_ORDINATES(4) AS \"max_y\" ");
        sb.Append("FROM (SELECT SDO_AGGR_MBR(").Append(geometryColumn).Append(") AS mbb FROM ");
        sb.Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geometryColumn).Append(" IS NOT NULL");

        var effective = query ?? new FeatureQuery();
        AppendWhereClause(sb, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        sb.Append(") t");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static string BuildGeometryWkbExpression(OracleLayerMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return "CAST(NULL AS BLOB)";
        }

        // SDO_UTIL.TO_WKBGEOMETRY emits 2D OGC WKB; any Z/M ordinates on the source geometry
        // are dropped (documented limitation matching other providers).
        return $"SDO_UTIL.TO_WKBGEOMETRY({mapping.QuotedGeometryColumn})";
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
            OracleIdentifier.EnsureValid(column, "attribute column");
            sb.Append(", ").Append(OracleIdentifier.Quote(column));
        }
    }

    private static void AppendWhereClause(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        // Same divergence as the SQL Server provider: the canonical Where text is parsed locally
        // so Postgres-styled SqlFilter fragments are not pasted into Oracle SQL.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), parameters);
            sb.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (query.SqlFilter is { } sqlFilter)
        {
            var rebound = RebindNamedParameters(sqlFilter.Sql, parameters.Count);
            sb.Append(" AND (").Append(rebound).Append(')');
            foreach (var param in sqlFilter.Parameters)
            {
                parameters.Add(param ?? DBNull.Value);
            }
        }
    }

    private static void AppendObjectIdsFilter(StringBuilder sb, OracleLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.ObjectIds.HasValue || query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var ids = query.ObjectIds.Value;
        var placeholders = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders[i] = ":p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(ids[i]);
        }

        sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn).Append(" IN (").Append(string.Join(", ", placeholders)).Append(')');
    }

    private static void AppendSpatialFilter(StringBuilder sb, OracleLayerMapping mapping, FeatureQuery query, List<object> parameters)
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
        var wkbParam = ":p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(filter.Geometry);

        // SDO_UTIL.FROM_WKBGEOMETRY produces a 2D SDO_GEOMETRY in the layer's CRS. Oracle
        // applies the layer SRID from the spatial index/metadata; the filter's SRID is not
        // injected into the SDO_GEOMETRY constructor here.
        var filterExpr = $"SDO_UTIL.FROM_WKBGEOMETRY({wkbParam})";

        var clause = filter.SpatialRelationship switch
        {
            SpatialRelationship.Intersects =>
                $"SDO_RELATE({geomCol}, {filterExpr}, 'mask=ANYINTERACT') = 'TRUE'",
            SpatialRelationship.EnvelopeIntersects =>
                $"SDO_RELATE(SDO_GEOM.SDO_MBR({geomCol}), {filterExpr}, 'mask=ANYINTERACT') = 'TRUE'",
            SpatialRelationship.Within =>
                $"SDO_RELATE({geomCol}, {filterExpr}, 'mask=INSIDE+COVEREDBY') = 'TRUE'",
            SpatialRelationship.Contains =>
                $"SDO_RELATE({geomCol}, {filterExpr}, 'mask=CONTAINS+COVERS') = 'TRUE'",
            SpatialRelationship.Disjoint =>
                $"NOT (SDO_RELATE({geomCol}, {filterExpr}, 'mask=ANYINTERACT') = 'TRUE')",
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the Oracle provider in this slice.")
        };

        sb.Append(" AND ").Append(clause);
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
            OracleIdentifier.EnsureValid(orderBy.Field, "order-by column");
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{OracleIdentifier.Quote(orderBy.Field)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static void AppendPagination(StringBuilder sb, OracleLayerMapping mapping, FeatureQuery query, List<object> parameters)
    {
        if (!query.Limit.HasValue && (!query.Offset.HasValue || query.Offset.Value <= 0))
        {
            return;
        }

        // Oracle 12c+ OFFSET / FETCH NEXT requires a deterministic ORDER BY for stable paging.
        // Fall back to the primary key when the caller did not supply one.
        if (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty)
        {
            sb.Append(" ORDER BY ").Append(mapping.QuotedPrimaryKeyColumn);
        }

        var offset = query.Offset.GetValueOrDefault(0);
        sb.Append(" OFFSET :p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture)).Append(" ROWS");
        parameters.Add(offset);

        if (query.Limit.HasValue)
        {
            sb.Append(" FETCH NEXT :p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture)).Append(" ROWS ONLY");
            parameters.Add(query.Limit.Value);
        }
    }

    private static string RebindNamedParameters(string sql, int startIndex)
    {
        var current = startIndex;
        // Rebind both @p<N> and :p<N> styles so fragments authored against other dialects line up.
        sql = AtPrefixedNamedParameterRegex().Replace(sql, _ => ":p" + (current++).ToString(CultureInfo.InvariantCulture));
        current = startIndex;
        return ColonPrefixedNamedParameterRegex().Replace(sql, _ => ":p" + (current++).ToString(CultureInfo.InvariantCulture));
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
                OracleIdentifier.EnsureValid(field, "WHERE column");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{OracleIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var compMatch = ComparisonRegex().Match(trimmed);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                OracleIdentifier.EnsureValid(field, "WHERE column");
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var value = ParseValueToken(compMatch.Groups["value"].Value);

                var paramName = ":p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(value);
                rendered.Add($"{OracleIdentifier.Quote(field)} {op} {paramName}");
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
    private static partial Regex AtPrefixedNamedParameterRegex();

    [GeneratedRegex(@":p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ColonPrefixedNamedParameterRegex();
}
