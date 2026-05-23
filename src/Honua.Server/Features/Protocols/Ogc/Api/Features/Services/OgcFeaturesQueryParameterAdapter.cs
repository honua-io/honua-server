// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Parameters for OGC API Features item queries.
/// </summary>
internal readonly record struct OgcFeaturesQueryParameters
{
    public required HttpRequest Request { get; init; }

    public string? Filter { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }

    public string? Bbox { get; init; }

    public string? Datetime { get; init; }

    public string? Ids { get; init; }

    public string? Properties { get; init; }

    public string? Sortby { get; init; }

    public string? Crs { get; init; }

    public string? F { get; init; }
}

/// <summary>
/// Converts OGC API Features item query parameters into the shared unified query model.
/// </summary>
internal sealed class OgcFeaturesQueryParameterAdapter(
    OgcFilterProcessor filterProcessor,
    ICommonQueryValidator queryValidator,
    IFilterExpressionTranslator filterExpressionTranslator,
    ILogger<OgcFeaturesQueryParameterAdapter> logger) : IQueryParameterAdapter<OgcFeaturesQueryParameters>
{
    private static readonly ImmutableHashSet<string> SortByCoreFields = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        FieldNames.ObjectId,
        "object_id",
        "id",
        "created_at",
        "updated_at");

    private readonly OgcFilterProcessor _filterProcessor = filterProcessor
        ?? throw new ArgumentNullException(nameof(filterProcessor));
    private readonly ICommonQueryValidator _queryValidator = queryValidator
        ?? throw new ArgumentNullException(nameof(queryValidator));
    private readonly IFilterExpressionTranslator _filterExpressionTranslator = filterExpressionTranslator
        ?? throw new ArgumentNullException(nameof(filterExpressionTranslator));
    private readonly ILogger<OgcFeaturesQueryParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "OGC-API-Features";

    public ProtocolLimits DefaultLimits => ProtocolLimits.OgcFeatures;

    public async Task<QueryAdapterResult> ConvertAsync(
        OgcFeaturesQueryParameters parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filterResult = await _filterProcessor.ProcessFiltersAsync(
                parameters.Request,
                layer,
                parameters.Filter,
                parameters.Bbox,
                parameters.Datetime,
                parameters.Crs,
                cancellationToken);
            if (!filterResult.IsSuccess)
            {
                return QueryAdapterResult.Failure(filterResult.ErrorMessage ?? "Invalid query parameters.");
            }

            var paginationResult = _queryValidator.ValidateAndNormalizePagination(parameters.Offset, parameters.Limit);
            if (!paginationResult.IsValid)
            {
                return QueryAdapterResult.Failure(paginationResult.ErrorMessage ?? "Invalid paging parameters.");
            }

            if (!OgcFeatureIdentifierResolver.TryCreateIdsFilter(
                    parameters.Ids,
                    layer,
                    _filterExpressionTranslator,
                    out var objectIds,
                    out var idsSqlFilter,
                    out var idsError))
            {
                return QueryAdapterResult.Failure(idsError ?? "Invalid ids parameter.");
            }

            if (!TryParseProperties(parameters.Properties, layer, out var selectedProperties, out var propertiesError))
            {
                return QueryAdapterResult.Failure(propertiesError ?? "Invalid properties parameter.");
            }

            if (!TryParseSortBy(parameters.Sortby, layer, out var orderBy, out var sortByError))
            {
                return QueryAdapterResult.Failure(sortByError ?? "Invalid sortby parameter.");
            }

            QueryFilter? queryFilter = null;
            var sqlFilter = CombineSqlFilters(filterResult.SqlFilter, idsSqlFilter);
            if (sqlFilter != null)
            {
                queryFilter = QueryFilter.FromSql(sqlFilter);
            }

            var metadata = new Dictionary<string, object>
            {
                ["f"] = parameters.F ?? "json",
                ["crsUri"] = filterResult.CrsDefinition.Uri,
                ["axisOrder"] = filterResult.CrsDefinition.AxisOrder,
                ["includeNullGeometry"] = filterResult.IncludeNullGeometry
            };

            var unifiedQuery = new UnifiedQuery
            {
                Filter = queryFilter,
                SpatialFilter = filterResult.SpatialFilter,
                TemporalFilter = filterResult.TemporalFilter,
                ObjectIds = objectIds,
                OutFields = selectedProperties,
                Offset = paginationResult.Value!.Offset,
                Limit = paginationResult.Value.Limit,
                OrderBy = orderBy,
                OutputCrs = QueryCrs.Create(
                    filterResult.CrsDefinition.Srid,
                    filterResult.CrsDefinition.AxisOrder,
                    filterResult.CrsDefinition.Uri),
                Hints = QueryHints.Create(
                    preferStreaming: paginationResult.Value.Limit > 200,
                    enableCaching: true,
                    requireExactCount: true),
                Extensions = ImmutableDictionary<string, object>.Empty
                    .Add("includeNullGeometry", filterResult.IncludeNullGeometry)
            };

            return QueryAdapterResult.Success(unifiedQuery, metadata);
        }
        catch (Exception ex)
        {
            OgcFeaturesPreparedAdaptersLog.QueryParameterConversionFailed(_logger, ex);
            return QueryAdapterResult.Failure("Invalid query parameters.");
        }
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="ConvertAsync(OgcFeaturesQueryParameters, LayerDefinition, CancellationToken)"/>.
    /// </summary>
    public async Task<QueryAdapterResult> ConvertAsync(
        OgcFeaturesQueryParameters parameters,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filterResult = await _filterProcessor.ProcessFiltersAsync(
                parameters.Request,
                resource,
                parameters.Filter,
                parameters.Bbox,
                parameters.Datetime,
                parameters.Crs,
                cancellationToken);
            if (!filterResult.IsSuccess)
            {
                return QueryAdapterResult.Failure(filterResult.ErrorMessage ?? "Invalid query parameters.");
            }

            var paginationResult = _queryValidator.ValidateAndNormalizePagination(parameters.Offset, parameters.Limit);
            if (!paginationResult.IsValid)
            {
                return QueryAdapterResult.Failure(paginationResult.ErrorMessage ?? "Invalid paging parameters.");
            }

            if (!OgcFeatureIdentifierResolver.TryCreateIdsFilter(
                    parameters.Ids,
                    resource,
                    _filterExpressionTranslator,
                    out var objectIds,
                    out var idsSqlFilter,
                    out var idsError))
            {
                return QueryAdapterResult.Failure(idsError ?? "Invalid ids parameter.");
            }

            if (!TryParseProperties(parameters.Properties, resource, out var selectedProperties, out var propertiesError))
            {
                return QueryAdapterResult.Failure(propertiesError ?? "Invalid properties parameter.");
            }

            if (!TryParseSortBy(parameters.Sortby, resource, out var orderBy, out var sortByError))
            {
                return QueryAdapterResult.Failure(sortByError ?? "Invalid sortby parameter.");
            }

            QueryFilter? queryFilter = null;
            var sqlFilter = CombineSqlFilters(filterResult.SqlFilter, idsSqlFilter);
            if (sqlFilter != null)
            {
                queryFilter = QueryFilter.FromSql(sqlFilter);
            }

            var metadata = new Dictionary<string, object>
            {
                ["f"] = parameters.F ?? "json",
                ["crsUri"] = filterResult.CrsDefinition.Uri,
                ["axisOrder"] = filterResult.CrsDefinition.AxisOrder,
                ["includeNullGeometry"] = filterResult.IncludeNullGeometry
            };

            var unifiedQuery = new UnifiedQuery
            {
                Filter = queryFilter,
                SpatialFilter = filterResult.SpatialFilter,
                TemporalFilter = filterResult.TemporalFilter,
                ObjectIds = objectIds,
                OutFields = selectedProperties,
                Offset = paginationResult.Value!.Offset,
                Limit = paginationResult.Value.Limit,
                OrderBy = orderBy,
                OutputCrs = QueryCrs.Create(
                    filterResult.CrsDefinition.Srid,
                    filterResult.CrsDefinition.AxisOrder,
                    filterResult.CrsDefinition.Uri),
                Hints = QueryHints.Create(
                    preferStreaming: paginationResult.Value.Limit > 200,
                    enableCaching: true,
                    requireExactCount: true),
                Extensions = ImmutableDictionary<string, object>.Empty
                    .Add("includeNullGeometry", filterResult.IncludeNullGeometry)
            };

            return QueryAdapterResult.Success(unifiedQuery, metadata);
        }
        catch (Exception ex)
        {
            OgcFeaturesPreparedAdaptersLog.QueryParameterConversionFailed(_logger, ex);
            return QueryAdapterResult.Failure("Invalid query parameters.");
        }
    }

    private static SqlFragment? CombineSqlFilters(SqlFragment? left, SqlFragment? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        return new SqlFragment(
            $"({left.Sql}) AND ({right.Sql})",
            left.Parameters.Concat(right.Parameters).ToArray());
    }

    private static bool TryParseProperties(
        string? rawProperties,
        LayerDefinition layer,
        out ImmutableArray<string>? properties,
        out string? error)
    {
        properties = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawProperties) ||
            string.Equals(rawProperties.Trim(), "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (HasEmptyCommaSeparatedToken(rawProperties))
        {
            error = "Parameter 'properties' contains an empty field name.";
            return false;
        }

        var tokens = rawProperties.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'properties' must contain at least one field name.";
            return false;
        }

        var fieldsByName = layer.VisibleAttributeFields
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var selected = ImmutableArray.CreateBuilder<string>(tokens.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (!IsSimpleFieldName(token))
            {
                error = $"Invalid properties field '{token}'.";
                return false;
            }

            if (!fieldsByName.TryGetValue(token, out var field))
            {
                error = $"Unknown properties field '{token}'.";
                return false;
            }

            if (seen.Add(field.Name))
            {
                selected.Add(field.Name);
            }
        }

        properties = selected.ToImmutable();
        return true;
    }

    private static bool TryParseSortBy(
        string? rawSortBy,
        LayerDefinition layer,
        out ImmutableArray<OrderByClause>? orderBy,
        out string? error)
    {
        orderBy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawSortBy))
        {
            return true;
        }

        if (HasEmptyCommaSeparatedToken(rawSortBy))
        {
            error = "Parameter 'sortby' contains an empty field expression.";
            return false;
        }

        var tokens = rawSortBy.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'sortby' must contain at least one field expression.";
            return false;
        }

        var normalized = new List<string>(tokens.Length);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                error = "Invalid sortby expression.";
                return false;
            }

            var ascending = true;
            if (token[0] is '+' or '-')
            {
                ascending = token[0] != '-';
                token = token[1..].Trim();
            }

            if (token.Length == 0)
            {
                error = "Invalid sortby expression.";
                return false;
            }

            if (!IsSimpleFieldName(token))
            {
                error = $"Invalid sortby field '{token}'.";
                return false;
            }

            normalized.Add(ascending ? token : $@"{token} DESC");
        }

        try
        {
            orderBy = OrderByParsing.ParseFeatureServerOrderBy(
                string.Join(",", normalized),
                layer,
                SortByCoreFields);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message.Replace("orderByFields", "sortby", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    private static bool TryParseProperties(
        string? rawProperties,
        MetadataV2Resource resource,
        out ImmutableArray<string>? properties,
        out string? error)
    {
        properties = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawProperties) ||
            string.Equals(rawProperties.Trim(), "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (HasEmptyCommaSeparatedToken(rawProperties))
        {
            error = "Parameter 'properties' contains an empty field name.";
            return false;
        }

        var tokens = rawProperties.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'properties' must contain at least one field name.";
            return false;
        }

        var fieldsByName = resource.SchemaFields
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var selected = ImmutableArray.CreateBuilder<string>(tokens.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (!IsSimpleFieldName(token))
            {
                error = $"Invalid properties field '{token}'.";
                return false;
            }

            if (!fieldsByName.TryGetValue(token, out var field))
            {
                error = $"Unknown properties field '{token}'.";
                return false;
            }

            if (seen.Add(field.Name))
            {
                selected.Add(field.Name);
            }
        }

        properties = selected.ToImmutable();
        return true;
    }

    private static bool TryParseSortBy(
        string? rawSortBy,
        MetadataV2Resource resource,
        out ImmutableArray<OrderByClause>? orderBy,
        out string? error)
    {
        orderBy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawSortBy))
        {
            return true;
        }

        if (HasEmptyCommaSeparatedToken(rawSortBy))
        {
            error = "Parameter 'sortby' contains an empty field expression.";
            return false;
        }

        var tokens = rawSortBy.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'sortby' must contain at least one field expression.";
            return false;
        }

        var normalized = new List<string>(tokens.Length);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                error = "Invalid sortby expression.";
                return false;
            }

            var ascending = true;
            if (token[0] is '+' or '-')
            {
                ascending = token[0] != '-';
                token = token[1..].Trim();
            }

            if (token.Length == 0)
            {
                error = "Invalid sortby expression.";
                return false;
            }

            if (!IsSimpleFieldName(token))
            {
                error = $"Invalid sortby field '{token}'.";
                return false;
            }

            normalized.Add(ascending ? token : $@"{token} DESC");
        }

        try
        {
            orderBy = OrderByParsing.ParseFeatureServerOrderBy(
                string.Join(",", normalized),
                resource,
                SortByCoreFields);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message.Replace("orderByFields", "sortby", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    private static bool IsSimpleFieldName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasEmptyCommaSeparatedToken(string value)
    {
        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            if (token.Trim().Length == 0)
            {
                return true;
            }
        }

        return false;
    }
}
