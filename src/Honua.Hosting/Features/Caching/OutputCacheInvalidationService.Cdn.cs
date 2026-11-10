// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.SharedCaching;

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
    // Data edits arrive here once per write; the CDN is told at most once per coalescing window.
    public async Task InvalidateEditedLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        await EvictLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        if (_cdnInvalidation is not null)
        {
            var paths = await CdnPathsForEditedLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
            await _cdnInvalidation.InvalidateAfterEditAsync(paths, cancellationToken).ConfigureAwait(false);
        }
    }

    // Routes match without regard to case and the CDN keys with regard to it, so a path prefix cannot reach every cached
    // spelling of an address. A change that may reduce access therefore invalidates everything; it is rare, and lifetimes are short.
    private Task InvalidateCdnForAdministrativeChangeAsync(CancellationToken cancellationToken)
        => _cdnInvalidation?.InvalidateNowAsync(CdnPaths.Everything, cancellationToken) ?? Task.CompletedTask;

    private async Task<string[]> CdnPathsForEditedLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        var resolvedServiceIds = await ResolveLayerServiceIdsAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        return CdnPaths.ForEditedLayer(
            layerId,
            string.IsNullOrWhiteSpace(serviceId) ? resolvedServiceIds : [.. resolvedServiceIds, serviceId.Trim()]);
    }
}
