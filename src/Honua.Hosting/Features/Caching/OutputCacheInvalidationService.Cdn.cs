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

    // Routes match without regard to case and the CDN keys with regard to it, so a path prefix cannot reach every cached
    // spelling of an address. A change that may reduce access therefore invalidates everything; it is rare, and lifetimes are short.
    private Task InvalidateCdnForAdministrativeChangeAsync(CancellationToken cancellationToken)
        => _cdnInvalidation?.InvalidateNowAsync(CdnPaths.Everything, cancellationToken) ?? Task.CompletedTask;

    private async Task<IReadOnlyList<string>> CdnPathsForLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        var resolvedServiceIds = await ResolveLayerServiceIdsAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var collectionIds = await ResolveLayerCollectionIdsAsync(layerId, cancellationToken).ConfigureAwait(false);
        var serviceIds = string.IsNullOrWhiteSpace(serviceId) ? resolvedServiceIds : [.. resolvedServiceIds, serviceId.Trim()];
        return
        [
            .. CdnPaths.ForLayer(layerId),
            .. serviceIds.Distinct(StringComparer.Ordinal).SelectMany(CdnPaths.ForService),
            .. collectionIds.SelectMany(CdnPaths.ForCollection),
        ];
    }
}
