// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Warehouse.Services;

public sealed class WarehouseQueryPlanExplainer : IWarehouseQueryPlanExplainer
{
    private const string RedactedMarker = "[REDACTED]";
    private readonly IWarehouseSourceCapabilityProvider _capabilityProvider;

    public WarehouseQueryPlanExplainer(IWarehouseSourceCapabilityProvider capabilityProvider)
    {
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
    }

    public async Task<WarehouseQueryPlan> ExplainAsync(
        WarehouseQueryPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var guardrails = request.Guardrails ?? WarehouseQueryGuardrails.Default;
        guardrails.Validate();

        var capabilities = await _capabilityProvider.GetCapabilitiesAsync(
            new WarehouseSourceCapabilityRequest
            {
                ProviderName = request.ProviderName,
                SourceId = request.SourceId,
                ExternalSourceUri = request.ExternalSourceUri,
                HasBboxCoveringColumn = !string.IsNullOrWhiteSpace(request.BboxColumn),
            },
            cancellationToken).ConfigureAwait(false);

        if (capabilities.Availability == WarehouseSourceAvailability.Unsupported)
        {
            throw new NotSupportedException(
                $"Warehouse provider '{capabilities.ProviderName}' is not supported for query plan explanation.");
        }

        var dialect = PlanDialect.ForProvider(capabilities.ProviderName);
        var quotedTarget = QuoteTableReference(request.TargetTableOrView, dialect);
        var parameters = ImmutableArray.CreateBuilder<WarehouseRedactedParameter>();
        var predicates = ImmutableArray.CreateBuilder<WarehousePushedPredicate>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var paramIndex = 1;

        var isH3 = request.H3Query.HasValue;
        var isStatistics = !isH3 && request.Query.OutStatistics is { IsDefaultOrEmpty: false };
        var operationKind = isH3 ? "h3_aggregation" : isStatistics ? "statistics" : "select";

        ValidateOperationCapabilities(request, capabilities, isH3);

        var (effectiveLimit, effectiveMaxCells, capped) = ResolveRowAndCellLimits(request, guardrails, isH3, warnings);
        var whereClauses = new List<string>();

        CollectEnforcedFilter(request.Query, capabilities, dialect, whereClauses, ref paramIndex, parameters, predicates);
        CollectAttributeFilter(request.Query, dialect, whereClauses, ref paramIndex, parameters, predicates);
        CollectObjectIdsFilter(request.Query, dialect, whereClauses, ref paramIndex, parameters, predicates);
        CollectTemporalFilter(request.Query, capabilities, dialect, whereClauses, ref paramIndex, parameters, predicates);
        var bboxPushdown = CollectSpatialAndBboxFilters(
            request, capabilities, dialect, whereClauses, ref paramIndex, parameters, predicates);

        var redactedSql = isH3
            ? BuildRedactedH3Sql(request, dialect, quotedTarget, whereClauses, effectiveMaxCells!.Value, ref paramIndex, parameters, predicates)
            : isStatistics
                ? BuildRedactedStatisticsSql(request.Query, dialect, quotedTarget, whereClauses, effectiveLimit, ref paramIndex, parameters, predicates)
                : BuildRedactedSelectSql(request.Query, dialect, quotedTarget, whereClauses, effectiveLimit, ref paramIndex, parameters, predicates);

        var requiredExtensions = capabilities.ProviderName == DataProviderNames.DuckDb
            ? WarehouseSourceCapabilityProvider.ResolveDuckDbExtensions(request.ExternalSourceUri, includeH3: isH3)
            : capabilities.RequiredExtensions;

        var builtParams = parameters.ToImmutable();
        var builtPredicates = predicates.ToImmutable();
        var builtWarnings = warnings.ToImmutable();

        var planHash = ComputePlanHash(
            capabilities.ProviderName,
            capabilities.SourceId,
            operationKind,
            redactedSql,
            builtParams,
            builtPredicates,
            bboxPushdown,
            isH3,
            effectiveLimit,
            effectiveMaxCells,
            guardrails);

        return new WarehouseQueryPlan
        {
            PlanHash = planHash,
            ProviderName = capabilities.ProviderName,
            SourceId = capabilities.SourceId,
            OperationKind = operationKind,
            RedactedSql = redactedSql,
            Parameters = builtParams,
            Predicates = builtPredicates,
            BboxPushdown = bboxPushdown,
            H3Pushdown = isH3,
            EffectiveRowLimit = effectiveLimit,
            EffectiveMaxH3Cells = effectiveMaxCells,
            AppliedGuardrails = guardrails,
            GuardrailCapped = capped,
            ReadOnly = true,
            RequiredExtensions = requiredExtensions,
            Warnings = builtWarnings,
        };
    }

    private static void ValidateOperationCapabilities(
        WarehouseQueryPlanRequest request,
        WarehouseEffectiveSourceCapabilities capabilities,
        bool isH3)
    {
        if (isH3)
        {
            if (!capabilities.Aggregation.SupportsH3Aggregation)
            {
                throw new NotSupportedException(
                    $"Warehouse provider '{capabilities.ProviderName}' does not support H3 aggregation pushdown.");
            }

            var h3 = request.H3Query!.Value;
            h3.RejectSummaryDefinitions();
            if (h3.Resolution is < H3AggregationQuery.MinResolution or > H3AggregationQuery.MaxResolution)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"H3 resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}.");
            }

            if (h3.KRingDistance is < 0 or > H3AggregationQuery.MaxKRingDistance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"H3 k-ring distance must be between 0 and {H3AggregationQuery.MaxKRingDistance}.");
            }
        }

        if (request.Query.TemporalFilter.HasValue && !capabilities.SupportsTemporalFilter)
        {
            throw new NotSupportedException(
                $"Warehouse provider '{capabilities.ProviderName}' does not support temporal filter pushdown.");
        }

        if (request.Query.EnforcedSqlFilter is not null && !capabilities.SupportsEnforcedPolicyPushdown)
        {
            throw new NotSupportedException(
                $"Warehouse provider '{capabilities.ProviderName}' cannot enforce server-side policy filters.");
        }

        if (request.Query.SpatialFilter is { } spatial)
        {
            if (!capabilities.SupportedSpatialPredicates.Contains(spatial.SpatialRelationship))
            {
                throw new NotSupportedException(
                    $"Warehouse provider '{capabilities.ProviderName}' does not support spatial predicate '{spatial.SpatialRelationship}'.");
            }

            if (!capabilities.CrsBehavior.SupportsCrossSridFilter
                && spatial.Srid is > 0
                && request.LayerSrid is > 0
                && spatial.Srid.Value != request.LayerSrid.Value)
            {
                throw new NotSupportedException(
                    $"Warehouse provider '{capabilities.ProviderName}' does not support cross-SRID spatial filters.");
            }
        }
    }

    private static (int EffectiveLimit, int? EffectiveMaxCells, bool Capped) ResolveRowAndCellLimits(
        WarehouseQueryPlanRequest request,
        WarehouseQueryGuardrails guardrails,
        bool isH3,
        ImmutableArray<string>.Builder warnings)
    {
        if (isH3)
        {
            var requestedCells = request.H3Query!.Value.MaxCells;
            var cappedCells = !requestedCells.HasValue || requestedCells.Value > guardrails.MaxH3Cells;
            var effectiveCells = Math.Min(requestedCells ?? guardrails.MaxH3Cells, guardrails.MaxH3Cells);
            if (cappedCells)
            {
                warnings.Add(FormattableString.Invariant(
                    $"H3 cell output bounded to guardrail maximum of {effectiveCells} cells."));
            }

            return (effectiveCells, effectiveCells, cappedCells);
        }

        var requestedLimit = request.Query.Limit;
        var cappedRows = !requestedLimit.HasValue || requestedLimit.Value > guardrails.MaxRows;
        var effectiveRows = Math.Min(requestedLimit ?? guardrails.MaxRows, guardrails.MaxRows);
        if (cappedRows)
        {
            warnings.Add(FormattableString.Invariant(
                $"Row output bounded to guardrail maximum of {effectiveRows} rows."));
        }

        return (effectiveRows, null, cappedRows);
    }

    private static void CollectEnforcedFilter(
        FeatureQuery query,
        WarehouseEffectiveSourceCapabilities capabilities,
        PlanDialect dialect,
        List<string> whereClauses,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (query.EnforcedSqlFilter is null)
        {
            return;
        }

        var placeholders = AddRedactedParameters(query.EnforcedSqlFilter.Parameters, dialect, ref paramIndex, parameters);
        whereClauses.Add(FormattableString.Invariant(
            $"/* enforced_policy({placeholders.Count} params) */"));
        predicates.Add(new WarehousePushedPredicate(
            "enforced_policy",
            capabilities.SupportsEnforcedPolicyPushdown,
            FormattableString.Invariant($"Server-enforced policy predicate with {placeholders.Count} redacted parameter(s).")));
    }

    private static void CollectAttributeFilter(
        FeatureQuery query,
        PlanDialect dialect,
        List<string> whereClauses,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var clauses = WhereClauseSplitter.SplitOnAnd(query.Where.Trim());
            var rendered = new List<string>(clauses.Count);
            foreach (var expr in LegacyWhereClauseParser.Parse(clauses, LegacyWhereGrammar.Warehouse))
            {
                rendered.Add(RenderRedactedWhereExpression(expr, dialect, ref paramIndex, parameters));
            }

            whereClauses.Add("(" + string.Join(" AND ", rendered) + ")");
            predicates.Add(new WarehousePushedPredicate(
                "attribute",
                true,
                FormattableString.Invariant($"Pushed down {rendered.Count} attribute predicate(s).")));
            return;
        }

        if (query.SqlFilter is not null)
        {
            var placeholders = AddRedactedParameters(query.SqlFilter.Parameters, dialect, ref paramIndex, parameters);
            whereClauses.Add(FormattableString.Invariant($"/* sql_filter({placeholders.Count} params) */"));
            predicates.Add(new WarehousePushedPredicate(
                "attribute",
                true,
                FormattableString.Invariant($"Pushed down translated filter with {placeholders.Count} redacted parameter(s).")));
        }
    }

    private static string RenderRedactedWhereExpression(
        LegacyWhereExpression expr,
        PlanDialect dialect,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters)
    {
        if (expr.Kind == LegacyWhereExpressionKind.True)
        {
            return "1=1";
        }

        if (expr.Kind == LegacyWhereExpressionKind.NullCheck)
        {
            var field = ValidateAndQuoteIdentifier(expr.Match.Groups["field"].Value, dialect);
            var notClause = string.IsNullOrWhiteSpace(expr.Match.Groups["not"].Value) ? string.Empty : "NOT ";
            return $"{field} IS {notClause}NULL";
        }

        if (expr.Kind == LegacyWhereExpressionKind.In)
        {
            var field = ValidateAndQuoteIdentifier(expr.Match.Groups["field"].Value, dialect);
            var placeholders = new List<string>();
            foreach (System.Text.RegularExpressions.Match valueMatch in LegacyWhereClauseParser.MatchInValues(expr.Match.Groups["values"].Value))
            {
                var value = LegacyWhereClauseParser.ParseValueToken(valueMatch.Value, LegacyWhereValuePolicy.NumericRequired);
                placeholders.Add(AddRedactedParameter(value, dialect, ref paramIndex, parameters));
            }

            return $"{field} IN ({string.Join(", ", placeholders)})";
        }

        if (expr.Kind == LegacyWhereExpressionKind.Comparison)
        {
            var field = ValidateAndQuoteIdentifier(expr.Match.Groups["field"].Value, dialect);
            var op = LegacyWhereClauseParser.NormalizeOperator(expr.Match.Groups["op"].Value, LegacyWhereGrammar.Warehouse);
            var value = LegacyWhereClauseParser.ParseValueToken(expr.Match.Groups["value"].Value, LegacyWhereValuePolicy.NumericRequired);
            var placeholder = AddRedactedParameter(value, dialect, ref paramIndex, parameters);
            return $"{field} {op} {placeholder}";
        }

        throw new ArgumentException("WHERE clause format not supported for warehouse query plan explanation.");
    }

    private static void CollectObjectIdsFilter(
        FeatureQuery query,
        PlanDialect dialect,
        List<string> whereClauses,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (query.ObjectIds is not { IsDefaultOrEmpty: false } objectIds)
        {
            return;
        }

        var placeholders = new string[objectIds.Length];
        for (var i = 0; i < objectIds.Length; i++)
        {
            placeholders[i] = AddRedactedParameter(objectIds[i], dialect, ref paramIndex, parameters);
        }

        whereClauses.Add($"{dialect.Quote("id")} IN ({string.Join(", ", placeholders)})");
        predicates.Add(new WarehousePushedPredicate(
            "object_ids",
            true,
            FormattableString.Invariant($"Pushed down IN filter for {objectIds.Length} object id(s).")));
    }

    private static void CollectTemporalFilter(
        FeatureQuery query,
        WarehouseEffectiveSourceCapabilities capabilities,
        PlanDialect dialect,
        List<string> whereClauses,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (!query.TemporalFilter.HasValue)
        {
            return;
        }

        var filter = query.TemporalFilter.Value;
        var column = ValidateAndQuoteIdentifier(filter.PropertyName, dialect);
        if (filter.Start.HasValue)
        {
            var p = AddRedactedParameter(filter.Start.Value, dialect, ref paramIndex, parameters);
            whereClauses.Add($"{column} >= {p}");
        }

        if (filter.End.HasValue)
        {
            var p = AddRedactedParameter(filter.End.Value, dialect, ref paramIndex, parameters);
            whereClauses.Add($"{column} <= {p}");
        }

        predicates.Add(new WarehousePushedPredicate(
            "temporal",
            capabilities.SupportsTemporalFilter,
            $"Pushed down temporal window on '{filter.PropertyName}'."));
    }

    private static bool CollectSpatialAndBboxFilters(
        WarehouseQueryPlanRequest request,
        WarehouseEffectiveSourceCapabilities capabilities,
        PlanDialect dialect,
        List<string> whereClauses,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (request.Query.SpatialFilter is not { } spatial)
        {
            return false;
        }

        var relationshipSupportsBbox = spatial.SpatialRelationship is not (
            SpatialRelationship.Disjoint
            or SpatialRelationship.WithinDistance
            or SpatialRelationship.BeyondDistance
            or SpatialRelationship.NearestNeighbor);
        var bboxCoveringPushdown = spatial.TryGetBboxCoveringBounds(request.LayerSrid, out var bounds)
            && !string.IsNullOrWhiteSpace(request.BboxColumn);

        if (bboxCoveringPushdown)
        {
            var bboxCol = ValidateAndQuoteIdentifier(request.BboxColumn!, dialect);
            var minX = AddRedactedParameter(bounds.MinX, dialect, ref paramIndex, parameters);
            var maxX = AddRedactedParameter(bounds.MaxX, dialect, ref paramIndex, parameters);
            var minY = AddRedactedParameter(bounds.MinY, dialect, ref paramIndex, parameters);
            var maxY = AddRedactedParameter(bounds.MaxY, dialect, ref paramIndex, parameters);
            whereClauses.Add($"{bboxCol}.xmax >= {minX} AND {bboxCol}.xmin <= {maxX} AND {bboxCol}.ymax >= {minY} AND {bboxCol}.ymin <= {maxY}");
            predicates.Add(new WarehousePushedPredicate(
                "bbox_covering",
                true,
                $"Pushed down GeoParquet bbox covering predicate on '{request.BboxColumn}'."));
        }

        if (capabilities.ProviderName == DataProviderNames.DuckDb
            && bboxCoveringPushdown
            && spatial.SpatialRelationship == SpatialRelationship.EnvelopeIntersects
            && spatial.AllowEnvelopeOnly)
        {
            return true;
        }

        var geomParam = AddRedactedParameter(spatial.Geometry, dialect, ref paramIndex, parameters);
        whereClauses.Add($"{dialect.SpatialPredicateName(spatial.SpatialRelationship)}({dialect.Quote("geom")}, {geomParam})");
        predicates.Add(new WarehousePushedPredicate(
            "spatial",
            true,
            $"Pushed down spatial predicate '{spatial.SpatialRelationship}'."));

        var bboxPushdown = bboxCoveringPushdown
            || (capabilities.ProviderName != DataProviderNames.DuckDb
                && capabilities.SupportsBboxPushdown
                && relationshipSupportsBbox
                && (spatial.IsSimpleEnvelope || spatial.SpatialRelationship == SpatialRelationship.EnvelopeIntersects));
        return bboxPushdown;
    }

    private static string BuildRedactedSelectSql(
        FeatureQuery query,
        PlanDialect dialect,
        string quotedTarget,
        List<string> whereClauses,
        int effectiveLimit,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        var projection = BuildProjectionList(query, dialect);
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(projection).Append(" FROM ").Append(quotedTarget);
        AppendWhere(sb, whereClauses);
        AppendOrderBy(sb, query, dialect, predicates);
        AppendLimitOffset(sb, effectiveLimit, query.Offset, dialect, ref paramIndex, parameters, predicates);
        return sb.ToString();
    }

    private static string BuildRedactedStatisticsSql(
        FeatureQuery query,
        PlanDialect dialect,
        string quotedTarget,
        List<string> whereClauses,
        int effectiveLimit,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        var selectCols = new List<string>();
        var groupCols = new List<string>();
        if (query.GroupByFields is { IsDefaultOrEmpty: false } groupBy)
        {
            foreach (var field in groupBy)
            {
                var quoted = ValidateAndQuoteIdentifier(field, dialect);
                selectCols.Add(quoted);
                groupCols.Add(quoted);
            }
        }

        foreach (var stat in query.OutStatistics!.Value)
        {
            var field = ValidateAndQuoteIdentifier(stat.OnStatisticField, dialect);
            var alias = ValidateAndQuoteIdentifier(stat.OutStatisticFieldName, dialect);
            selectCols.Add($"{stat.StatisticType.ToString().ToUpperInvariant()}({field}) AS {alias}");
        }

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", selectCols)).Append(" FROM ").Append(quotedTarget);
        AppendWhere(sb, whereClauses);
        if (groupCols.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", groupCols));
        }

        predicates.Add(new WarehousePushedPredicate(
            "statistics",
            true,
            FormattableString.Invariant($"Pushed down {query.OutStatistics.Value.Length} aggregate statistic(s).")));
        AppendOrderBy(sb, query, dialect, predicates);
        AppendLimitOffset(sb, effectiveLimit, query.Offset, dialect, ref paramIndex, parameters, predicates);
        return sb.ToString();
    }

    private static string BuildRedactedH3Sql(
        WarehouseQueryPlanRequest request,
        PlanDialect dialect,
        string quotedTarget,
        List<string> whereClauses,
        int effectiveMaxCells,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        var h3 = request.H3Query!.Value;
        var resParam = AddRedactedParameter(h3.Resolution, dialect, ref paramIndex, parameters);
        var cellExpr = dialect.H3CellExpression(resParam);
        var boundaryExpr = dialect.H3BoundaryExpression("_h3cell");

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(dialect.H3CellIndexProjection("_h3cell")).Append(" AS ").Append(dialect.Quote("cellIndex"));
        sb.Append(", ").Append(boundaryExpr).Append(" AS ").Append(dialect.Quote("cellGeometry"));
        if (h3.OutStatistics is { IsDefaultOrEmpty: false } stats)
        {
            foreach (var stat in stats)
            {
                var field = ValidateAndQuoteIdentifier(stat.OnStatisticField, dialect);
                var alias = ValidateAndQuoteIdentifier(stat.OutStatisticFieldName, dialect);
                sb.Append(", ").Append(stat.StatisticType.ToString().ToUpperInvariant()).Append('(').Append(field).Append(") AS ").Append(alias);
            }
        }
        else
        {
            sb.Append(", COUNT(*) AS ").Append(dialect.Quote("count"));
        }

        sb.Append(" FROM (SELECT *, ").Append(cellExpr).Append(" AS _h3cell FROM ").Append(quotedTarget);
        AppendWhere(sb, whereClauses);
        sb.Append(") _src");

        if (h3.PolyfillGeometry is { Length: > 0 })
        {
            var polyParam = AddRedactedParameter(h3.PolyfillGeometry, dialect, ref paramIndex, parameters);
            sb.Append(" WHERE _h3cell IN (SELECT cell FROM ").Append(dialect.H3PolyfillExpression(polyParam, resParam)).Append(')');
            predicates.Add(new WarehousePushedPredicate("h3_polyfill", true, "Pushed down H3 polyfill cell filter."));
        }

        sb.Append(" GROUP BY _h3cell");
        if (h3.KRingDistance is > 0)
        {
            var kParam = AddRedactedParameter(h3.KRingDistance.Value, dialect, ref paramIndex, parameters);
            sb.Append(" /* k_ring(").Append(kParam).Append(") */");
            predicates.Add(new WarehousePushedPredicate("h3_kring", true, "Pushed down H3 k-ring neighbor expansion."));
        }

        sb.Append(" ORDER BY COUNT(*) DESC");
        AppendLimitOffset(sb, effectiveMaxCells, null, dialect, ref paramIndex, parameters, predicates);

        predicates.Add(new WarehousePushedPredicate(
            "h3_aggregation",
            true,
            FormattableString.Invariant($"Pushed down H3 hexagonal aggregation at resolution {h3.Resolution}.")));
        return sb.ToString();
    }

    private static string BuildProjectionList(FeatureQuery query, PlanDialect dialect)
    {
        if (query.ExcludeAttributes)
        {
            return $"{dialect.Quote("id")}, {dialect.Quote("geom")}";
        }

        if (query.OutFields is { IsDefaultOrEmpty: false } outFields)
        {
            var cols = new List<string>(outFields.Length + 2)
            {
                dialect.Quote("id"),
                dialect.Quote("geom"),
            };
            foreach (var field in outFields.OrderBy(static f => f, StringComparer.Ordinal))
            {
                if (field == "*")
                {
                    return "*";
                }

                cols.Add(ValidateAndQuoteIdentifier(field, dialect));
            }

            return string.Join(", ", cols);
        }

        return "*";
    }

    private static void AppendWhere(StringBuilder sb, List<string> whereClauses)
    {
        if (whereClauses.Count == 0)
        {
            return;
        }

        sb.Append(" WHERE ").Append(string.Join(" AND ", whereClauses));
    }

    private static void AppendOrderBy(
        StringBuilder sb,
        FeatureQuery query,
        PlanDialect dialect,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        if (query.OrderBy is not { IsDefaultOrEmpty: false } orderBy)
        {
            return;
        }

        var clauses = new List<string>(orderBy.Length);
        foreach (var clause in orderBy)
        {
            var quoted = ValidateAndQuoteIdentifier(clause.Field, dialect);
            clauses.Add($"{quoted} {(clause.Ascending ? "ASC" : "DESC")}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", clauses));
        predicates.Add(new WarehousePushedPredicate(
            "order_by",
            true,
            FormattableString.Invariant($"Pushed down ORDER BY on {orderBy.Length} field(s).")));
    }

    private static void AppendLimitOffset(
        StringBuilder sb,
        int limit,
        int? offset,
        PlanDialect dialect,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters,
        ImmutableArray<WarehousePushedPredicate>.Builder predicates)
    {
        var limitPlaceholder = AddRedactedParameter(limit, dialect, ref paramIndex, parameters);
        sb.Append(" LIMIT ").Append(limitPlaceholder);
        if (offset is > 0)
        {
            var offsetPlaceholder = AddRedactedParameter(offset.Value, dialect, ref paramIndex, parameters);
            sb.Append(" OFFSET ").Append(offsetPlaceholder);
        }

        predicates.Add(new WarehousePushedPredicate(
            "paging",
            true,
            FormattableString.Invariant($"Pushed down LIMIT guardrail ({limit}).")));
    }

    private static List<string> AddRedactedParameters(
        IReadOnlyList<object?> values,
        PlanDialect dialect,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters)
    {
        var placeholders = new List<string>(values.Count);
        foreach (var value in values)
        {
            placeholders.Add(AddRedactedParameter(value, dialect, ref paramIndex, parameters));
        }

        return placeholders;
    }

    private static string AddRedactedParameter(
        object? value,
        PlanDialect dialect,
        ref int paramIndex,
        ImmutableArray<WarehouseRedactedParameter>.Builder parameters)
    {
        var placeholder = dialect.FormatPlaceholder(paramIndex++);
        var (valueType, redactedValue) = DescribeRedactedValue(value);
        parameters.Add(new WarehouseRedactedParameter(placeholder, valueType, redactedValue));
        return placeholder;
    }

    private static (string ValueType, string RedactedValue) DescribeRedactedValue(object? value) => value switch
    {
        null or DBNull => ("null", RedactedMarker),
        byte[] bytes => ("bytes", FormattableString.Invariant($"[REDACTED:bytes({bytes.Length})]")),
        bool => ("boolean", RedactedMarker),
        int or long or short => ("integer", RedactedMarker),
        double or float or decimal => ("number", RedactedMarker),
        DateTime or DateTimeOffset => ("timestamp", RedactedMarker),
        _ => ("string", RedactedMarker),
    };

    private static string QuoteTableReference(string tableOrView, PlanDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(tableOrView))
        {
            throw new ArgumentException("Target table or view name cannot be empty.", nameof(tableOrView));
        }

        var parts = tableOrView.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3)
        {
            throw new ArgumentException($"Invalid target table reference '{tableOrView}'.", nameof(tableOrView));
        }

        var quoted = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            quoted[i] = ValidateAndQuoteIdentifier(parts[i], dialect);
        }

        return string.Join('.', quoted);
    }

    private static string ValidateAndQuoteIdentifier(string identifier, PlanDialect dialect)
    {
        if (!FeatureFieldNameSyntax.IsValid(identifier))
        {
            throw new ArgumentException($"Unsupported warehouse identifier '{identifier}'.", nameof(identifier));
        }

        return dialect.Quote(identifier);
    }

    private static string ComputePlanHash(
        string providerName,
        string sourceId,
        string operationKind,
        string redactedSql,
        ImmutableArray<WarehouseRedactedParameter> parameters,
        ImmutableArray<WarehousePushedPredicate> predicates,
        bool bboxPushdown,
        bool h3Pushdown,
        int effectiveRowLimit,
        int? effectiveMaxH3Cells,
        WarehouseQueryGuardrails guardrails)
    {
        var canonical = new StringBuilder();
        canonical.Append(providerName).Append('|')
            .Append(sourceId).Append('|')
            .Append(operationKind).Append('|')
            .Append(redactedSql).Append('|')
            .Append(bboxPushdown).Append('|')
            .Append(h3Pushdown).Append('|')
            .Append(effectiveRowLimit.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(effectiveMaxH3Cells?.ToString(CultureInfo.InvariantCulture) ?? "none").Append('|')
            .Append(guardrails.MaxRows.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(guardrails.MaxH3Cells.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(guardrails.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(guardrails.MaxScanBytes?.ToString(CultureInfo.InvariantCulture) ?? "none");

        foreach (var parameter in parameters)
        {
            canonical.Append('|').Append(parameter.Placeholder).Append(':').Append(parameter.ValueType).Append(':').Append(parameter.RedactedValue);
        }

        foreach (var predicate in predicates)
        {
            canonical.Append('|').Append(predicate.Kind).Append(':').Append(predicate.PushedDown).Append(':').Append(predicate.Summary);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private sealed class PlanDialect
    {
        private readonly string _provider;

        private PlanDialect(string provider) => _provider = provider;

        public static PlanDialect ForProvider(string provider) => new(provider);

        public string Quote(string identifier)
            => _provider == DataProviderNames.Databricks
                ? "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`"
                : SqlIdentifier.QuoteIdentifier(identifier);

        public string FormatPlaceholder(int oneBasedIndex) => _provider switch
        {
            DataProviderNames.DuckDb => FormattableString.Invariant($"${oneBasedIndex}"),
            DataProviderNames.Snowflake => "?",
            DataProviderNames.Databricks => FormattableString.Invariant($":p{oneBasedIndex - 1}"),
            DataProviderNames.Redshift => FormattableString.Invariant($"@p{oneBasedIndex - 1}"),
            _ => FormattableString.Invariant($"${oneBasedIndex}"),
        };

        public string SpatialPredicateName(SpatialRelationship relationship)
            => _provider switch
            {
                DataProviderNames.Snowflake => $"ST_{relationship.ToString().ToUpperInvariant()}",
                DataProviderNames.Databricks => $"st_{relationship.ToString().ToLowerInvariant()}",
                _ => $"ST_{relationship}",
            };

        public string H3CellExpression(string resolutionPlaceholder) => _provider switch
        {
            DataProviderNames.DuckDb => $"h3_latlng_to_cell(ST_Y(ST_Centroid({Quote("geom")})), ST_X(ST_Centroid({Quote("geom")})), {resolutionPlaceholder})",
            DataProviderNames.Snowflake => $"H3_POINT_TO_CELL_STRING(ST_CENTROID({Quote("geom")}), {resolutionPlaceholder})",
            DataProviderNames.Databricks => $"h3_longlatash3string(st_x(st_centroid({Quote("geom")})), st_y(st_centroid({Quote("geom")})), {resolutionPlaceholder})",
            _ => throw new NotSupportedException($"Provider '{_provider}' does not support H3 cell expressions."),
        };

        public string H3CellIndexProjection(string alias) => _provider switch
        {
            DataProviderNames.DuckDb => $"h3_h3_to_string({alias})",
            _ => alias,
        };

        public string H3BoundaryExpression(string alias) => _provider switch
        {
            DataProviderNames.DuckDb => $"ST_AsGeoJSON(ST_GeomFromText(h3_cell_to_boundary_wkt({alias})))",
            DataProviderNames.Snowflake => $"ST_ASGEOJSON(H3_CELL_TO_BOUNDARY({alias}))",
            DataProviderNames.Databricks => $"h3_boundaryasgeojson({alias})",
            _ => throw new NotSupportedException($"Provider '{_provider}' does not support H3 boundary expressions."),
        };

        public string H3PolyfillExpression(string polyParam, string resolutionPlaceholder) => _provider switch
        {
            DataProviderNames.DuckDb => $"UNNEST(h3_polygon_wkt_to_cells(ST_AsText(ST_GeomFromWKB({polyParam})), {resolutionPlaceholder}))",
            DataProviderNames.Snowflake => $"TABLE(FLATTEN(INPUT => H3_POLYGON_TO_CELLS_STRINGS(TO_GEOGRAPHY({polyParam}, TRUE), {resolutionPlaceholder})))",
            DataProviderNames.Databricks => $"EXPLODE(h3_polyfillash3string(st_astext(st_geomfromwkb(unhex({polyParam}), 4326)), {resolutionPlaceholder}))",
            _ => throw new NotSupportedException($"Provider '{_provider}' does not support H3 polyfill expressions."),
        };
    }
}
