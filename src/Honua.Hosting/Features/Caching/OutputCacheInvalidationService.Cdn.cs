// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.SharedCaching;

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
    // Data edits arrive here once per write, so the CDN is told at most once per layer per coalescing window.
    public async Task InvalidateEditedLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        await EvictLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        if (_cdnInvalidation is not null)
        {
            var paths = await CdnPathsForLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
            await _cdnInvalidation.InvalidateCoalescedAsync($"layer:{layerId}", paths, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InvalidateCdnForLayerAsync(string? serviceId, int? layerId, CancellationToken cancellationToken)
    {
        if (_cdnInvalidation is null)
        {
            return;
        }

        var paths = layerId is { } id
            ? await CdnPathsForLayerAsync(serviceId, id, cancellationToken).ConfigureAwait(false)
            : CdnPaths.Everything;
        await _cdnInvalidation.InvalidateNowAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    private async Task InvalidateCdnForServiceCatalogAsync(string? serviceId, int[] layerIds, CancellationToken cancellationToken)
    {
        if (_cdnInvalidation is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(serviceId) && layerIds.Length == 0)
        {
            await _cdnInvalidation.InvalidateNowAsync(CdnPaths.Everything, cancellationToken).ConfigureAwait(false);
            return;
        }

        var paths = new List<string>();
        foreach (var layerId in layerIds)
        {
            paths.AddRange(await CdnPathsForLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            paths.AddRange(CdnPaths.ForService(serviceId.Trim()));
        }

        await _cdnInvalidation.InvalidateNowAsync(paths.Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    private Task InvalidateCdnForSceneAsync(string? sceneId, CancellationToken cancellationToken)
        => _cdnInvalidation is null || string.IsNullOrWhiteSpace(sceneId)
            ? Task.CompletedTask
            : _cdnInvalidation.InvalidateNowAsync(CdnPaths.ForScene(sceneId.Trim()), cancellationToken);

    private async Task<IReadOnlyList<string>> CdnPathsForLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        var serviceIds = await ResolveLayerServiceIdsAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var collectionIds = await ResolveLayerCollectionIdsAsync(layerId, cancellationToken).ConfigureAwait(false);
        return
        [
            .. CdnPaths.ForLayer(layerId),
            .. serviceIds.SelectMany(CdnPaths.ForService),
            .. collectionIds.SelectMany(CdnPaths.ForCollection),
        ];
    }
}
