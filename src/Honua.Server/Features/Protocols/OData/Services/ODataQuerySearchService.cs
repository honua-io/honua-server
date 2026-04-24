// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Composite service that combines OData query and search functionality.
/// Provides a single service interface for OData operations while maintaining
/// architectural compliance by reducing handler dependencies.
/// </summary>
internal sealed class ODataQuerySearchService
{
    private readonly ODataQueryService _queryService;
    private readonly ODataSearchService _searchService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataQuerySearchService"/> class.
    /// </summary>
    public ODataQuerySearchService(
        ODataQueryService queryService,
        ODataSearchService searchService)
    {
        _queryService = queryService;
        _searchService = searchService;
    }

    /// <summary>
    /// Builds a feature query from OData parameters with proper validation and conversion.
    /// </summary>
    public async Task<(FeatureQuery Query, string? Error)> BuildFeatureQueryAsync(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        LayerDefinition layer,
        string? select = null,
        string? expand = null,
        bool? count = null,
        string? compute = null,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.BuildFeatureQueryAsync(
            filter,
            orderby,
            resultRecordCount,
            resultOffset,
            layer,
            select,
            expand,
            count,
            compute,
            format,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies basic filtering to layer collections using simple OData expressions.
    /// </summary>
    public IEnumerable<LayerDefinition> ApplyBasicFilter(
        IEnumerable<LayerDefinition> layers,
        string filter)
    {
        return _queryService.ApplyBasicFilter(layers, filter);
    }

    /// <summary>
    /// Applies field selection to result objects using an AOT-compatible approach.
    /// </summary>
    public object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        return _queryService.ApplyFieldSelection(data, select);
    }

    /// <summary>
    /// Processes $expand to fetch related entities for features.
    /// </summary>
    public async Task<Dictionary<long, Dictionary<string, object?[]>>> ProcessExpandAsync(
        string expand,
        LayerDefinition layer,
        long[] objectIds,
        CancellationToken cancellationToken)
        => await ProcessExpandAsync(expand, layer, objectIds, context: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Processes $expand to fetch related entities for features.
    /// </summary>
    public async Task<Dictionary<long, Dictionary<string, object?[]>>> ProcessExpandAsync(
        string expand,
        LayerDefinition layer,
        long[] objectIds,
        HttpContext? context,
        CancellationToken cancellationToken = default)
    {
        return await _searchService.ProcessExpandAsync(expand, layer, objectIds, context, cancellationToken);
    }

    /// <summary>
    /// Handles OData $search full-text search operations with PostgreSQL text search.
    /// </summary>
    public async Task<ODataSearchResult> HandleSearchAsync(
        int layerId,
        string searchExpression,
        string baseUrl,
        int? top = null,
        int? skip = null,
        bool? count = null,
        string? filter = null,
        string? orderby = null,
        string? select = null,
        string? expand = null,
        CancellationToken cancellationToken = default)
        => await HandleSearchAsync(
            layerId,
            searchExpression,
            baseUrl,
            top,
            skip,
            count,
            filter,
            orderby,
            select,
            expand,
            context: null,
            cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Handles OData $search full-text search operations with PostgreSQL text search.
    /// </summary>
    public async Task<ODataSearchResult> HandleSearchAsync(
        int layerId,
        string searchExpression,
        string baseUrl,
        int? top = null,
        int? skip = null,
        bool? count = null,
        string? filter = null,
        string? orderby = null,
        string? select = null,
        string? expand = null,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        return await _searchService.HandleSearchAsync(
            layerId,
            searchExpression,
            baseUrl,
            top,
            skip,
            count,
            filter,
            orderby,
            select,
            expand,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Handles OData $apply aggregation operations with support for various transformations.
    /// </summary>
    public async Task<ODataAggregationResult> HandleApplyAsync(
        int layerId,
        string applyExpression,
        string? filter,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        return await _searchService.HandleApplyAsync(layerId, applyExpression, filter, baseUrl, cancellationToken);
    }
}
