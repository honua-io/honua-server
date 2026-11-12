// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal sealed class ODataQuerySearchService
{
    private readonly ODataQueryService _queryService;
    private readonly ODataSearchService _searchService;

    public ODataQuerySearchService(
        ODataQueryService queryService,
        ODataSearchService searchService)
    {
        _queryService = queryService;
        _searchService = searchService;
    }

    public async Task<(FeatureQuery Query, string? Error)> BuildFeatureQueryAsync(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        MetadataV2Object resource,
        string? select = null,
        string? expand = null,
        bool? count = null,
        string? compute = null,
        string? format = null,
        BoundingBox? bbox = null,
        int? publicLayerId = null,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.BuildFeatureQueryAsync(
            filter,
            orderby,
            resultRecordCount,
            resultOffset,
            resource,
            select,
            expand,
            count,
            compute,
            format,
            bbox,
            publicLayerId,
            cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<Dictionary<string, object?>> ApplyBasicFilter(
        IEnumerable<Dictionary<string, object?>> layerPayloads,
        string filter)
    {
        return _queryService.ApplyBasicFilter(layerPayloads, filter);
    }

    public object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        return _queryService.ApplyFieldSelection(data, select);
    }

    public async Task<Dictionary<long, Dictionary<string, object?[]>>> ProcessExpandAsync(
        string expand,
        MetadataV2Object resource,
        int sourceStorageLayerId,
        long[] objectIds,
        HttpContext? context,
        CancellationToken cancellationToken = default)
    {
        return await _searchService.ProcessExpandAsync(
            expand,
            resource,
            sourceStorageLayerId,
            objectIds,
            context,
            cancellationToken);
    }

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

    public async Task<ODataAggregationResult> HandleApplyAsync(
        ODataApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        return await _searchService.HandleApplyAsync(request, cancellationToken);
    }
}
