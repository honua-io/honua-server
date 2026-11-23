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

    public Task InvalidateServiceCatalogAsync(
        string? serviceId,
        IEnumerable<int>? layerIds,
        CancellationToken cancellationToken)
    {
        var normalizedServiceId = string.IsNullOrWhiteSpace(serviceId)
            ? null
            : serviceId.Trim().ToLowerInvariant();
        return CompleteAdministrativeInvalidationAsync(normalizedServiceId, [.. layerIds?.Distinct() ?? []], cancellationToken);
    }

    public Task InvalidateEverythingAsync(CancellationToken cancellationToken)
        => CompleteAdministrativeInvalidationAsync(normalizedServiceId: null, [], cancellationToken);

    // Once started, an invalidation outlives its request: stopping part-way would leave origin copies, the CDN or promoted
    // exports behind a change that may already be saved. The caller still learns that its request ended.
    private async Task CompleteAdministrativeInvalidationAsync(string? normalizedServiceId, int[] layerIdList, CancellationToken requestAborted)
    {
        await EvictServiceCatalogAsync(normalizedServiceId, layerIdList, CancellationToken.None).ConfigureAwait(false);
        await InvalidateCdnForAdministrativeChangeAsync(CancellationToken.None).ConfigureAwait(false);
        await NotifyPublicExportAccessChangeAsync(normalizedServiceId, layerIdList, CancellationToken.None).ConfigureAwait(false);
        requestAborted.ThrowIfCancellationRequested();
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
