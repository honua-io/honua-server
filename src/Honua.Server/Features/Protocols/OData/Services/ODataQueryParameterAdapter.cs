// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.OData.Services;

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

    /// <summary>
    /// Parses the OData filter into a <see cref="FilterExpression"/> using the V2-aware
    /// <see cref="IFilterExpressionService.ParseAndNormalize"/> overload, validates $select against
    /// <see cref="MetadataV2Resource.SchemaFields"/>, and stores the filter as a
    /// <see cref="QueryFilter"/>. SQL translation is deferred to the downstream processor's
    /// SQL last-mile bridge (#1035): see metadata-v2-cutover-plan.md.
    /// </summary>
    public Task<QueryAdapterResult> ConvertAsync(
        ODataQueryParameters parameters,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            QueryFilter? filter = null;
            if (!string.IsNullOrWhiteSpace(parameters.Filter))
            {
                var parseResult = _filterExpressionService.ParseAndNormalize(
                    FilterLanguage.OData,
                    parameters.Filter,
                    resource);
                if (!parseResult.IsSuccess || parseResult.Expression == null)
                {
                    return Task.FromResult(QueryAdapterResult.Failure(
                        parseResult.ErrorMessage ?? "Invalid OData filter expression."));
                }

                filter = QueryFilter.FromExpression(
                    parseResult.Expression,
                    new FilterSource(parameters.Filter, FilterLanguage.OData, ProtocolName));
            }

            var outFields = ResolveSelectedFieldsV2(
                parameters.Select,
                parameters.Expand,
                parameters.Compute,
                resource,
                out var selectError);
            if (selectError != null)
            {
                return Task.FromResult(QueryAdapterResult.Failure(selectError));
            }

            var orderBy = ParseOrderByV2(parameters.OrderBy, resource);
            if (!orderBy.HasValue || orderBy.Value.IsDefaultOrEmpty)
            {
                orderBy = ImmutableArray.Create(new OrderByClause(
                    FieldNames.ObjectId,
                    ascending: true));
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

    private static ImmutableArray<string>? ResolveSelectedFieldsV2(
        string? select,
        string? expand,
        string? compute,
        MetadataV2Resource resource,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(select) || string.Equals(select.Trim(), "*", StringComparison.Ordinal))
        {
            return null;
        }

        var availableFields = resource.SchemaFields.ToDictionary(
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

    private static ImmutableArray<OrderByClause>? ParseOrderByV2(string? orderBy, MetadataV2Resource resource)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        var schemaFields = resource.SchemaFields.ToDictionary(
            field => field.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawField in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawField.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 2)
            {
                throw new ArgumentException("Invalid $orderby expression.");
            }

            var field = parts[0];
            if (!IsValidODataFieldName(field))
            {
                throw new ArgumentException($"Invalid field name in $orderby: {field}");
            }

            var ascending = true;
            if (parts.Length > 1)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (!parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Invalid sort direction in $orderby: {parts[1]}. Use 'asc' or 'desc'.");
                }
            }

            var resolvedField = schemaFields.TryGetValue(field, out var v2Field) ? v2Field.Name : field;
            // Pass the schema-declared field type so the SQL builder emits a typed cast
            // ($orderby=population desc on an integer column must sort numerically, not
            // lexically — otherwise "20" < "3" via the default TEXT-based attribute path).
            clauses.Add(new OrderByClause(resolvedField, ascending, v2Field?.Type));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
    }

    private static bool IsValidODataFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var first = fieldName[0];
        if (!(char.IsLetter(first) || first == '_'))
        {
            return false;
        }

        for (var i = 1; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
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
