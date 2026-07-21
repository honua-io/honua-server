// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;

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
        GuardUnsupportedTemporalFilter(query);

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
        GuardUnsupportedTemporalFilter(query);

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
        GuardUnsupportedTemporalFilter(query);

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

        sb.Append("SELECT ");
        sb.Append("envelope.STPointN(1).").Append(xProperty).Append(" AS [min_x], ");
        sb.Append("envelope.STPointN(1).").Append(yProperty).Append(" AS [min_y], ");
        sb.Append("envelope.STPointN(3).").Append(xProperty).Append(" AS [max_x], ");
        sb.Append("envelope.STPointN(3).").Append(yProperty).Append(" AS [max_y] ");
        sb.Append("FROM (SELECT ").Append(mapping.GeometryTypeToken).Append("::EnvelopeAggregate(").Append(geometryColumn).Append(") AS envelope FROM ");
        sb.Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geometryColumn).Append(" IS NOT NULL");

        var effective = query ?? new FeatureQuery();
        GuardUnsupportedTemporalFilter(effective);
        AppendWhereClause(sb, effective, parameters);
        AppendObjectIdsFilter(sb, mapping, effective, parameters);
        AppendSpatialFilter(sb, mapping, effective, parameters);

        sb.Append(") AS aggregated");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Rejects a <see cref="FeatureQuery.TemporalFilter"/> the SQL Server provider cannot
    /// translate. Temporal filters arrive on <see cref="FeatureQuery"/> from OGC API Features
    /// (<c>datetime</c>), STAC search, and OData time-window queries. The provider does not yet
    /// emit T-SQL temporal predicates, so the filter is surfaced as an eager
    /// <see cref="NotSupportedException"/> — never silently dropped, which would return rows
    /// outside the requested window (a correctness/data-leak bug). Mirrors the fail-loud
    /// contract of the MySQL/Redshift/Databricks providers (temporal GA hardening, #2429).
    /// </summary>
    private static void GuardUnsupportedTemporalFilter(FeatureQuery query)
    {
        if (query.TemporalFilter.HasValue)
        {
            throw new NotSupportedException(
                "Temporal filters are not supported by the SQL Server provider in this slice. " +
                "Apply temporal filtering in the calling layer or use a PostGIS-backed layer.");
        }
    }

    private static string BuildGeometryWkbExpression(SqlServerLayerMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return "CAST(NULL AS varbinary(max))";
        }

        // STAsBinary() emits OGC 2D WKB; any Z/M ordinates on the source geometry are dropped
        // (documented limitation matching other providers — see OracleFeatureQueryBuilder.cs).
        // Use AsBinaryZM() instead if 3D/measured geometry support is required in a future slice.
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
        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres translator today. Passing
        // a Postgres-emitted fragment through here would yield invalid T-SQL (JSON path operators,
        // unquoted identifiers, dollar-sign placeholders), so we re-parse Where using this provider's
        // own SQL Server-aware parser whenever it is supplied.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where!.Trim(), parameters);
            sb.Append(" AND (").Append(parameterized).Append(')');
            return;
        }

        if (query.SqlFilter is not null)
        {
            // The shared ISqlFilterTranslator pipeline registers a PostgreSQL translator; the
            // SqlFilter produced by it contains Postgres-specific SQL (JSONB ->> operators,
            // ::casts, ST_* functions, double-quoted identifiers) that is not valid T-SQL. The
            // Oracle provider documents and enforces the same restriction (OracleFeatureQueryBuilder.cs).
            // Passing a Postgres-flavored fragment through here would produce an opaque SqlException
            // rather than a clear, actionable error, so we reject it here instead.
            // Callers that need SQL-Server-specific filtered queries should populate the canonical
            // FeatureQuery.Where text (e.g. via the FeatureServer 'where' parameter), which is
            // re-parsed by this provider's own T-SQL-aware parser.
            throw new NotSupportedException(
                "Translated FeatureQuery.SqlFilter fragments are not executable against the SQL Server provider. " +
                "The shared ISqlFilterTranslator pipeline emits Postgres-flavored SQL which is not valid T-SQL. " +
                "Route the request through a protocol path that populates the canonical Where text (FeatureServer 'where'), " +
                "or restrict CQL2/FES/OData $filter usage to providers that register their own ISqlFilterTranslator.");
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
            // Esri semantics: esriSpatialRelWithin = filter geometry is within feature geometry;
            // esriSpatialRelContains = filter geometry contains feature geometry. Lead with the
            // filter geometry to match the canonical PostGIS reference (#2068). Reversing the
            // operands inverts the relationship and returns the wrong (typically empty) result set.
            SpatialRelationship.Within =>
                $"{filterExpr}.STWithin({geomCol}) = 1",
            SpatialRelationship.Contains =>
                $"{filterExpr}.STContains({geomCol}) = 1",
            SpatialRelationship.Disjoint =>
                $"{geomCol}.STDisjoint({filterExpr}) = 1",
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the SQL Server provider in this slice.")
        };

        sb.Append(" AND ").Append(clause);
    }

    private static string BuildFilterGeometryExpression(SpatialFilter filter, SqlServerLayerMapping mapping, List<object> parameters)
    {
        // SQL Server spatial methods (STIntersects, STWithin, …) return NULL when the two
        // operands have different SRIDs, causing the predicate to evaluate as NOT 1=1 and the
        // query to silently return zero rows. The MySQL provider throws NotSupportedException for
        // cross-SRID filters. Mirror that contract here so callers receive a clear error instead
        // of an empty result set.
        if ((filter.Srid is > 0 && mapping.Srid.HasValue && filter.Srid.Value != mapping.Srid.Value)
            || (filter.Srid is > 0 && !mapping.Srid.HasValue))
        {
            var layerSridDescription = mapping.Srid.HasValue
                ? mapping.Srid.Value.ToString(CultureInfo.InvariantCulture)
                : "unset (null)";
            throw new NotSupportedException(
                $"Cross-SRID spatial filter is not supported by the SQL Server provider: " +
                $"filter SRID {filter.Srid.Value} differs from layer SRID {layerSridDescription}. " +
                $"Pre-project the filter geometry to the layer's SRID before submitting the request.");
        }

        // SQL Server's geometry/geography static parsers require an explicit SRID. Use the
        // filter's SRID when supplied; otherwise inherit the layer SRID for safe comparison.
        // If neither is available the expression resolves to SRID 0, which causes SQL Server
        // spatial predicates to return NULL (not false) for all rows -> silent zero-row result.
        // Throw rather than silently returning nothing, mirroring the MySQL/cross-SRID contract.
        if (filter.Srid is not > 0 && !mapping.Srid.HasValue)
        {
            throw new NotSupportedException(
                "Spatial filter cannot be built: the filter geometry does not carry an SRID and the " +
                "layer mapping does not specify a storage SRID. SQL Server spatial predicates return " +
                "NULL (not false) when the geometry SRID is 0, silently producing zero rows. " +
                "Set the layer SRID in the mapping or provide an explicit SRID on the spatial filter.");
        }

        var isGeography = mapping.GeometryColumnType == SqlServerGeometryColumnType.Geography;

        // SQL Server's geography type uses the left-hand rule: a polygon's interior lies to the
        // left of each ring's traversal direction, i.e. exterior rings must be counter-clockwise
        // (and holes clockwise). Esri clients commonly emit clockwise-exterior polygons, which the
        // geography parser interprets as the polygon's complement (the "everything but this" ring)
        // and rejects with error 24205 when that complement spans more than a hemisphere. Normalize
        // polygon/multipolygon winding to CCW-exterior before serialization so every geography
        // predicate sees the intended region. The geometry (planar) type is orientation-insensitive,
        // so only its embedded EWKB SRID metadata is removed.
        var wkb = isGeography
            ? WkbSridNormalizer.RemoveEmbeddedSrid(SqlServerGeographyWinding.NormalizeToCcwExterior(filter.Geometry))
            : WkbSridNormalizer.RemoveEmbeddedSrid(filter.Geometry);

        var wkbParam = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(wkb);

        var srid = filter.Srid is > 0 ? filter.Srid.Value : mapping.Srid ?? 0;
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);

        return isGeography
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
                SqlServerIdentifier.EnsureValid(field, "WHERE column");
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                rendered.Add($"{SqlServerIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var inMatch = InExpressionRegex().Match(trimmed);
            if (inMatch.Success)
            {
                var field = inMatch.Groups["field"].Value;
                SqlServerIdentifier.EnsureValid(field, "WHERE column");
                var placeholders = new List<string>();
                foreach (Match valueMatch in InValueRegex().Matches(inMatch.Groups["values"].Value))
                {
                    placeholders.Add("@p" + parameters.Count.ToString(CultureInfo.InvariantCulture));
                    parameters.Add(ParseValueToken(valueMatch.Value));
                }
                rendered.Add($"{SqlServerIdentifier.Quote(field)} IN ({string.Join(", ", placeholders)})");
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
    // [a-zA-Z0-9_] used by ComparisonRegex / NullCheckRegex so column names that contain
    // or begin with "and" (e.g. start_and_end, and_flag) are not falsely split as
    // logical AND boundaries. Underscore is a legal T-SQL identifier character and must
    // not separate tokens.
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

    [GeneratedRegex(@"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InExpressionRegex();

    [GeneratedRegex(@"'(?:''|[^'])*'|-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex InValueRegex();

}
