// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Protocol parameter shape for OData feature queries.
/// </summary>
internal readonly record struct ODataQueryParameters
{
    public string? Filter { get; init; }

    public string? OrderBy { get; init; }

    public int? Top { get; init; }

    public int? Skip { get; init; }

    public string? Select { get; init; }

    public string? Expand { get; init; }

    public bool? Count { get; init; }

    public string? Compute { get; init; }

    public string? Format { get; init; }
}

/// <summary>
/// Converts OData query parameters into the shared unified query model.
/// </summary>
internal sealed class ODataQueryParameterAdapter(
    IFilterExpressionService filterExpressionService,
    ILogger<ODataQueryParameterAdapter> logger) : IQueryParameterAdapter<ODataQueryParameters>
{
    private readonly IFilterExpressionService _filterExpressionService = filterExpressionService
        ?? throw new ArgumentNullException(nameof(filterExpressionService));
    private readonly ILogger<ODataQueryParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "OData";

    public ProtocolLimits DefaultLimits => ProtocolLimits.OData;

    public Task<QueryAdapterResult> ConvertAsync(
        ODataQueryParameters parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            QueryFilter? filter = null;
            if (!string.IsNullOrWhiteSpace(parameters.Filter))
            {
                var parseResult = _filterExpressionService.Parse(FilterLanguage.OData, parameters.Filter);
                if (!parseResult.IsSuccess || parseResult.Expression == null)
                {
                    return Task.FromResult(QueryAdapterResult.Failure(
                        parseResult.ErrorMessage ?? "Invalid OData filter expression."));
                }

                var translationResult = _filterExpressionService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess || translationResult.SqlFilter == null)
                {
                    return Task.FromResult(QueryAdapterResult.Failure(
                        translationResult.ErrorMessage ?? "Invalid OData filter expression."));
                }

                filter = QueryFilter.FromSql(
                    translationResult.SqlFilter,
                    new FilterSource(parameters.Filter, FilterLanguage.OData, ProtocolName));
            }

            var outFields = ResolveSelectedFields(
                parameters.Select,
                parameters.Expand,
                parameters.Compute,
                layer,
                out var selectError);
            if (selectError != null)
            {
                return Task.FromResult(QueryAdapterResult.Failure(selectError));
            }

            var orderBy = OrderByParsing.ParseODataOrderBy(parameters.OrderBy, layer);
            if (!orderBy.HasValue || orderBy.Value.IsDefaultOrEmpty)
            {
                orderBy = ImmutableArray.Create(new OrderByClause(
                    FieldNames.ObjectId,
                    ascending: true,
                    fieldType: FieldType.BigInteger));
            }

            var metadata = new Dictionary<string, object>
            {
                ["format"] = parameters.Format ?? "application/json",
                ["count"] = parameters.Count ?? false
            };

            if (!string.IsNullOrWhiteSpace(parameters.Expand))
            {
                metadata["expand"] = parameters.Expand;
            }

            if (!string.IsNullOrWhiteSpace(parameters.Compute))
            {
                metadata["compute"] = parameters.Compute;
            }

            var unifiedQuery = new UnifiedQuery
            {
                Filter = filter,
                OutFields = outFields,
                Offset = parameters.Skip,
                Limit = parameters.Top,
                OrderBy = orderBy,
                Hints = QueryHints.Create(
                    preferStreaming: (parameters.Top ?? DefaultLimits.DefaultResultCount) > DefaultLimits.DefaultResultCount,
                    enableCaching: string.IsNullOrWhiteSpace(parameters.Expand),
                    requireExactCount: parameters.Count == true)
            };

            return Task.FromResult(QueryAdapterResult.Success(unifiedQuery, metadata));
        }
        catch (ArgumentException ex)
        {
            ODataPreparedAdaptersLog.InvalidQueryParameters(_logger, ex);
            return Task.FromResult(QueryAdapterResult.Failure(ex.Message));
        }
        catch (Exception ex)
        {
            ODataPreparedAdaptersLog.QueryParameterConversionFailed(_logger, ex);
            return Task.FromResult(QueryAdapterResult.Failure("Invalid OData query parameters."));
        }
    }

    private static ImmutableArray<string>? ResolveSelectedFields(
        string? select,
        string? expand,
        string? compute,
        LayerDefinition layer,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(select) || string.Equals(select.Trim(), "*", StringComparison.Ordinal))
        {
            return null;
        }

        var availableFields = layer.AttributeFields.ToDictionary(
            field => field.Name,
            StringComparer.OrdinalIgnoreCase);
        var allowedVirtualSelections = GetAllowedVirtualSelections(expand, compute);
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (allowedVirtualSelections.Contains(segment))
            {
                continue;
            }

            if (!availableFields.TryGetValue(segment, out var field))
            {
                error = $"Unknown field in $select: {segment}";
                return null;
            }

            if (seen.Add(field.Name))
            {
                builder.Add(field.Name);
            }
        }

        return builder.Count == 0 ? ImmutableArray<string>.Empty : builder.ToImmutable();
    }

    private static HashSet<string> GetAllowedVirtualSelections(string? expand, string? compute)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ObjectId",
            "LayerId",
            "Geometry"
        };

        if (!string.IsNullOrWhiteSpace(expand))
        {
            foreach (var segment in expand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    allowed.Add(segment);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(compute))
        {
            foreach (var segment in compute.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var aliasIndex = segment.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                if (aliasIndex < 0)
                {
                    continue;
                }

                var alias = segment[(aliasIndex + 4)..].Trim();
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    allowed.Add(alias);
                }
            }
        }

        return allowed;
    }
}
