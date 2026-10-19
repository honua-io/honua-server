// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Db.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Translates a <see cref="FeatureQuery"/> into parameterized T-SQL for the SQL Server
/// spatial provider. All identifiers are validated and bracket-quoted; all literal values
/// flow through SQL parameters.
/// </summary>
internal static partial class SqlServerFeatureQueryBuilder
{
    internal const string ProviderLabel = "SQL Server";

    /// <summary>
    /// Builds a SELECT that returns the primary key, the geometry as WKB (or NULL when no
    /// geometry column is configured), and any requested attribute columns.
    /// </summary>
    public static ParameterizedQuery BuildSelectQuery(SqlServerLayerMapping mapping, FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(attributeColumns);
        query = NormalizeConstraints(query, translationOfWhere: null, translationProved: false, mapping.PrimaryKeyColumn);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        var projectedAttributes = ResolveProjectedAttributeColumns(query, attributeColumns);
        if (query.Distinct)
        {
            // Distinct values are attribute tuples. A real primary key or geometry would
            // make every row unique, so the reader still sees a non-null id (literal 0)
            // and a null geometry while DISTINCT applies only to the projected attributes.
            sb.Append("SELECT DISTINCT 0 AS [__objectid], CAST(NULL AS varbinary(max)) AS [__geometry]");
            AppendQuotedAttributeColumns(sb, projectedAttributes);
        }
        else
        {
            sb.Append("SELECT ").Append(mapping.QuotedPrimaryKeyColumn).Append(" AS [__objectid]");

            var geometryExpr = BuildGeometryWkbExpression(mapping);
            sb.Append(", ").Append(geometryExpr).Append(" AS [__geometry]");
            AppendQuotedAttributeColumns(sb, projectedAttributes);
        }

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        if (query.Distinct)
        {
            if (IsPaging(query))
            {
                AppendDistinctOrderBy(sb, query, projectedAttributes);
            }
        }
        else
        {
            AppendOrderByClause(sb, query);
        }

        AppendPagination(sb, mapping, query, parameters, suppressPrimaryKeyTiebreaker: query.Distinct);

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT COUNT(*) for the same query envelope as <see cref="BuildSelectQuery"/>.
    /// </summary>
    public static ParameterizedQuery BuildCountQuery(SqlServerLayerMapping mapping, FeatureQuery query)
        => BuildCountQuery(mapping, query, attributeColumns: null);

    /// <summary>
    /// Builds a count for <paramref name="query"/>. When <see cref="FeatureQuery.Distinct"/> is set,
    /// the count is the number of distinct projected attribute tuples, not the raw row count.
    /// </summary>
    public static ParameterizedQuery BuildCountQuery(
        SqlServerLayerMapping mapping,
        FeatureQuery query,
        IReadOnlyList<string>? attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        query = NormalizeConstraints(query, translationOfWhere: null, translationProved: false, mapping.PrimaryKeyColumn);
        GuardUnsupportedTemporalFilter(query);

        var sb = new StringBuilder();
        var parameters = new List<object>();

        if (!query.Distinct)
        {
            sb.Append("SELECT COUNT_BIG(*) FROM ").Append(mapping.QuotedTableReference);
            sb.Append(" WHERE 1=1");

            AppendWhereClause(sb, query, parameters);
            AppendObjectIdsFilter(sb, mapping, query, parameters);
            AppendSpatialFilter(sb, mapping, query, parameters);

            return new ParameterizedQuery(sb.ToString(), parameters);
        }

        var projectedAttributes = attributeColumns is null
            ? []
            : ResolveProjectedAttributeColumns(query, attributeColumns);
        sb.Append("SELECT COUNT_BIG(*) FROM (SELECT DISTINCT ");
        if (projectedAttributes.Count == 0)
        {
            sb.Append('1');
        }
        else
        {
            sb.Append(string.Join(", ", projectedAttributes.Select(SqlServerIdentifier.QuoteAttribute)));
        }

        sb.Append(" FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        AppendWhereClause(sb, query, parameters);
        AppendObjectIdsFilter(sb, mapping, query, parameters);
        AppendSpatialFilter(sb, mapping, query, parameters);
        sb.Append(") AS distinct_values");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds a SELECT that returns the primary key for matching rows. Used for object-id
    /// listings when only identifiers are needed.
    /// </summary>
    public static ParameterizedQuery BuildObjectIdsQuery(SqlServerLayerMapping mapping, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        query = NormalizeConstraints(query, translationOfWhere: null, translationProved: false, mapping.PrimaryKeyColumn);
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

        var effective = NormalizeConstraints(query ?? new FeatureQuery(), translationOfWhere: null, translationProved: false, mapping.PrimaryKeyColumn);
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

    /// <summary>
    /// Recovers provider-neutral constraints from a Postgres-translated <see cref="FeatureQuery.SqlFilter"/>.
    /// A pure integral <c>IN</c> list is lifted only when its target is the object id or the
    /// layer primary key. Attribute <c>IN</c> lists and <c>NOT IN</c> stay unlifted.
    /// </summary>
    internal static FeatureQuery NormalizeConstraints(
        FeatureQuery query,
        SqlFragment? translationOfWhere,
        bool translationProved,
        string? primaryKeyColumn = null)
        => NonPostgresObjectIdConstraintNormalizer.Normalize(
            query,
            translationOfWhere,
            translationProved,
            ProviderLabel,
            primaryKeyColumn);

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

    private static List<string> ResolveProjectedAttributeColumns(FeatureQuery query, IReadOnlyList<string> attributeColumns)
    {
        if (query.ExcludeAttributes || attributeColumns.Count == 0)
        {
            return [];
        }

        // Null/default OutFields projects every column. An explicit empty array projects
        // none. IsEmpty throws on a default ImmutableArray, so test IsDefault first.
        IEnumerable<string> columns = attributeColumns;
        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefault)
        {
            var requestedOutFields = query.OutFields.Value;
            if (requestedOutFields.IsEmpty)
            {
                return [];
            }

            var requested = new HashSet<string>(requestedOutFields, StringComparer.OrdinalIgnoreCase);
            columns = attributeColumns.Where(c => requested.Contains(c));
        }

        return columns.ToList();
    }

    private static void AppendQuotedAttributeColumns(StringBuilder sb, IReadOnlyList<string> projectedAttributes)
    {
        foreach (var column in projectedAttributes)
        {
            sb.Append(", ").Append(SqlServerIdentifier.QuoteAttribute(column));
        }
    }

    private static void AppendWhereClause(StringBuilder sb, FeatureQuery query, List<object> parameters)
    {
        if (query.TextSearch is { } search)
        {
            var predicate = FeatureTextSearchSql.Build(search, SqlServerIdentifier.QuoteAttribute, text =>
            {
                var marker = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(text);
                return marker;
            }, (column, value) => $"CHARINDEX({value}, {column})");
            sb.Append(" AND ").Append(predicate);
        }

        // Prefer the canonical Where text. SqlFilter on FeatureQuery is produced by the shared
        // ISqlFilterTranslator pipeline, which only registers a Postgres translator today. Passing
        // a Postgres-emitted fragment through here would yield invalid T-SQL (JSON path operators,
        // unquoted identifiers, dollar-sign placeholders), so we re-parse Where using this provider's
        // own SQL Server-aware parser whenever it is supplied.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            if (query.SqlFilter is not null)
            {
                throw new NotSupportedException(
                    "SQL Server cannot safely combine canonical Where text with a translated SqlFilter. " +
                    "Submit the additional constraints through provider-neutral FeatureQuery properties.");
            }

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
        // SQL Server rejects statements with more than 2100 parameters. Keep a short IN list
        // and bind a longer id set as one JSON array.
        if (ids.Length > 2000 || parameters.Count + ids.Length > 2000)
        {
            var placeholder = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(FormatObjectIdJsonArray(ids));
            sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn)
                .Append(" IN (SELECT CAST([value] AS bigint) FROM OPENJSON(")
                .Append(placeholder)
                .Append("))");
            return;
        }

        var placeholders = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders[i] = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(ids[i]);
        }

        sb.Append(" AND ").Append(mapping.QuotedPrimaryKeyColumn).Append(" IN (").Append(string.Join(", ", placeholders)).Append(')');
    }

    private static string FormatObjectIdJsonArray(ImmutableArray<long> ids)
    {
        var json = new StringBuilder(ids.Length * 4);
        json.Append('[');
        for (var i = 0; i < ids.Length; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(ids[i].ToString(CultureInfo.InvariantCulture));
        }

        json.Append(']');
        return json.ToString();
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
        // so it skips winding normalization, but both STGeomFromWKB overloads still need their
        // Z/M dimension flags translated from Honua's canonical EWKB high-bit flavor to the
        // ISO/OGC type-code offset SQL Server expects (see SqlServerGeographyWinding's doc
        // comments); WkbSridNormalizer only strips the SRID word and otherwise byte-preserves
        // dimension flags as given, so it runs after the ISO-flavor translation as a final,
        // idempotent SRID-removal pass.
        var wkb = isGeography
            ? WkbSridNormalizer.RemoveEmbeddedSrid(SqlServerGeographyWinding.NormalizeToCcwExterior(filter.Geometry))
            : WkbSridNormalizer.RemoveEmbeddedSrid(SqlServerGeographyWinding.NormalizeToPlainWkb(filter.Geometry));

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
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            clauses.Add($"{SqlServerIdentifier.QuoteAttribute(orderBy.Field)} {direction}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    private static bool IsPaging(FeatureQuery query)
        => query.Limit.HasValue || query.Offset is > 0;

    /// <summary>
    /// Orders a DISTINCT projection by columns that appear in the select list. SQL Server
    /// rejects ORDER BY expressions that are not projected, and the primary key is not part
    /// of a distinct-values tuple.
    /// </summary>
    private static void AppendDistinctOrderBy(StringBuilder sb, FeatureQuery query, IReadOnlyList<string> projectedAttributes)
    {
        var clauses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (query.OrderBy.HasValue && !query.OrderBy.Value.IsDefaultOrEmpty)
        {
            foreach (var orderBy in query.OrderBy.Value)
            {
                var match = projectedAttributes.FirstOrDefault(column =>
                    string.Equals(column, orderBy.Field, StringComparison.OrdinalIgnoreCase));
                if (match is null || !seen.Add(match))
                {
                    continue;
                }

                var direction = orderBy.Ascending ? "ASC" : "DESC";
                clauses.Add($"{SqlServerIdentifier.QuoteAttribute(match)} {direction}");
            }
        }

        foreach (var column in projectedAttributes.Where(seen.Add))
        {
            clauses.Add($"{SqlServerIdentifier.QuoteAttribute(column)} ASC");
        }

        sb.Append(" ORDER BY ");
        sb.Append(clauses.Count == 0 ? "[__objectid]" : string.Join(", ", clauses));
    }

    private static void AppendPagination(
        StringBuilder sb,
        SqlServerLayerMapping mapping,
        FeatureQuery query,
        List<object> parameters,
        bool suppressPrimaryKeyTiebreaker = false)
    {
        if (!IsPaging(query))
        {
            return;
        }

        // SQL Server requires ORDER BY before OFFSET / FETCH NEXT. When the caller did not
        // supply one, fall back to the primary key. A caller ORDER BY is left unchanged:
        // appending the key would change tie-breaking the caller did not request.
        // Distinct projections order their selected columns and suppress this fallback
        // because the key is not in that select list.
        if (!suppressPrimaryKeyTiebreaker
            && (!query.OrderBy.HasValue || query.OrderBy.Value.IsDefaultOrEmpty))
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
