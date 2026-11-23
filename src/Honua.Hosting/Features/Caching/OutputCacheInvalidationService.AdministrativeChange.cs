// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

// One administrative change tells the CDN and the export revocation check once, after every origin copy it touches is gone.
// Told per layer, the CDN repeats the same full invalidation and can refill from a layer that is not evicted yet.
internal sealed partial class OutputCacheInvalidationService
{
    private static readonly string[] ServiceCatalogTags =
    [
        "service-directory",
        "service-metadata",
        "tiles",
        "layer-metadata",
        "layer-styles",
        "metadata",
        "ogc-metadata",
        "ogc-tiles",
        "ogc-maps",
        "stac-metadata",
        "mvt-tiles",
        "terrain",
    ];

    public async Task InvalidateServiceCatalogAsync(
        string? serviceId,
        IEnumerable<int>? layerIds,
        CancellationToken cancellationToken)
    {
        var normalizedServiceId = string.IsNullOrWhiteSpace(serviceId)
            ? null
            : serviceId.Trim().ToLowerInvariant();
        int[] layerIdList = [.. layerIds?.Distinct() ?? []];

        await PurgeCdnAfterAsync(EvictServiceCatalogAsync(normalizedServiceId, layerIdList, cancellationToken)).ConfigureAwait(false);
        await NotifyPublicExportAccessChangeAsync(normalizedServiceId, layerIdList, cancellationToken).ConfigureAwait(false);
    }

    public async Task InvalidateEverythingAsync(CancellationToken cancellationToken)
    {
        await PurgeCdnAfterAsync(EvictServiceCatalogAsync(normalizedServiceId: null, [], cancellationToken)).ConfigureAwait(false);
        await NotifyPublicExportAccessChangeAsync(serviceId: null, layerIds: null, cancellationToken).ConfigureAwait(false);
    }

    // The change is committed before eviction starts, so a request that ends early still has to reach the CDN.
    private async Task PurgeCdnAfterAsync(Task eviction)
    {
        try
        {
            await eviction.ConfigureAwait(false);
        }
        finally
        {
            await InvalidateCdnForAdministrativeChangeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task EvictServiceCatalogAsync(
        string? normalizedServiceId,
        int[] layerIdList,
        CancellationToken cancellationToken)
    {
        foreach (var layerId in layerIdList)
        {
            await EvictStorageLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
            await EvictLayerAsync(normalizedServiceId, layerId, cancellationToken).ConfigureAwait(false);
        }

        var tags = new List<string>(ServiceCatalogTags);
        var responsePatterns = new List<string>();
        if (normalizedServiceId is not null)
        {
            tags.Add($"service:{normalizedServiceId}");
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerServicePattern(normalizedServiceId));
        }
        else if (layerIdList.Length == 0)
        {
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerPattern());
            responsePatterns.Add(ResponseCacheUtilities.BuildOgcPattern());
            responsePatterns.Add(ResponseCacheUtilities.BuildODataPattern());
        }

        await Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken),
            EvictCatalogCacheAsync(normalizedServiceId, layerIdList, cancellationToken)).ConfigureAwait(false);
    }
}
