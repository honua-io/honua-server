// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.SharedCaching;

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
    // Data edits arrive here once per write; the CDN is told at most once per coalescing window.
    public async Task InvalidateEditedLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        try
        {
            await EvictLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
            if (_cdnInvalidation is not null)
            {
                var paths = await CdnPathsForEditedLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
                await InvalidateCdnAfterEditAsync(paths, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cdnInvalidation is not null)
        {
            // The edit is committed even though its request gave up: what can be named without a lookup goes out with the next
            // window at once, and the names of its service follow when the lookup completes without the request.
            string[] knownServices = string.IsNullOrWhiteSpace(serviceId) ? [] : [serviceId.Trim()];
            _cdnInvalidation.InvalidateWithNextWindow(_cdnInvalidation.TagsEnabled
                ? CdnCacheTags.InvalidationsForEditedLayer(layerId, knownServices)
                : CdnPaths.ForEditedLayer(layerId, knownServices));
            _ = InvalidateResolvedWithNextWindowAsync(_cdnInvalidation, serviceId, layerId);
            throw;
        }
    }

    private async Task InvalidateResolvedWithNextWindowAsync(CdnInvalidationService cdnInvalidation, string? serviceId, int layerId)
    {
        var paths = await CdnPathsForEditedLayerAsync(serviceId, layerId, CancellationToken.None).ConfigureAwait(false);
        cdnInvalidation.InvalidateWithNextWindow(paths);
    }

    public async Task InvalidateEditedStorageLayerAsync(int storageLayerId, CancellationToken cancellationToken)
    {
        await EvictStorageLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        var paths = await CdnPathsForEditedStorageLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        await InvalidateCdnAfterEditAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    public async Task InvalidateEditedCollectionAsync(string collectionId, CancellationToken cancellationToken)
    {
        await EvictCollectionAsync(collectionId, cancellationToken).ConfigureAwait(false);
        await InvalidateCdnAfterEditAsync(CdnPathsForEditedOgcSurface(), cancellationToken).ConfigureAwait(false);
    }

    public async Task InvalidateEditedOgcMetadataAsync(CancellationToken cancellationToken)
    {
        await EvictOgcMetadataAsync(cancellationToken).ConfigureAwait(false);
        await InvalidateCdnAfterEditAsync(CdnPathsForEditedOgcSurface(), cancellationToken).ConfigureAwait(false);
    }

    private string[] CdnPathsForEditedOgcSurface()
        => _cdnInvalidation is { TagsEnabled: true }
            ? CdnCacheTags.InvalidationsForOgcSurface
            : CdnPaths.EveryOgcSurface;

    private async Task<string[]> CdnPathsForEditedStorageLayerAsync(int storageLayerId, CancellationToken cancellationToken)
    {
        if (_cdnInvalidation is not { TagsEnabled: true })
        {
            return CdnPaths.Everything;
        }

        var snapshot = await ReadStorageInvalidationSnapshotAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return CdnPaths.Everything;
        }

        var paths = new HashSet<string>(CdnCacheTags.InvalidationsForEditedLayer(storageLayerId, []), StringComparer.Ordinal);
        foreach (var publication in snapshot.PublicationsForStorageLayer(storageLayerId))
        {
            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
            {
                return CdnPaths.Everything;
            }

            paths.UnionWith(CdnCacheTags.InvalidationsForEditedLayer(
                publication.LayerIndex ?? storageLayerId, [service.Metadata.Name, service.EffectivePublicName]));
        }

        return [.. paths];
    }

    private Task InvalidateCdnAfterEditAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
        => _cdnInvalidation?.InvalidateAfterEditAsync(paths, cancellationToken) ?? Task.CompletedTask;

    // Routes match without regard to case and the CDN keys with regard to it, so a path prefix cannot reach every cached
    // spelling of an address. A change that may reduce access therefore invalidates everything; it is rare, and lifetimes are short.
    private Task InvalidateCdnForAdministrativeChangeAsync(CancellationToken cancellationToken)
        => _cdnInvalidation?.InvalidateNowAsync(CdnPaths.Everything, cancellationToken) ?? Task.CompletedTask;

    private async Task<string[]> CdnPathsForEditedLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        if (_cdnInvalidation is { TagsEnabled: true })
        {
            var serviceNames = await PublishedServiceNamesAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
            var tagInvalidations = CdnCacheTags.InvalidationsForEditedLayer(layerId, serviceNames);
            if (tagInvalidations.Length > 0)
            {
                return tagInvalidations;
            }
        }

        var resolvedServiceIds = await ResolveLayerServiceIdsAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        return CdnPaths.ForEditedLayer(
            layerId,
            string.IsNullOrWhiteSpace(serviceId) ? resolvedServiceIds : [.. resolvedServiceIds, serviceId.Trim()]);
    }

    // A response is tagged with the name its service is published under, which may differ from the name an edit arrives with.
    // Layer numbers are per service, so an edit that names its service never reaches the other services' layer of that number.
    private async Task<string[]> PublishedServiceNamesAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editedService = serviceId?.Trim();
        if (!string.IsNullOrEmpty(editedService))
        {
            names.Add(editedService);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IMetadataV2GraphProvider>() is { } graphProvider)
            {
                var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                foreach (var publication in snapshot.Graph.Publications.Where(publication => publication.LayerIndex == layerId))
                {
                    if (snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service)
                        && (string.IsNullOrEmpty(editedService) || Names(service).Contains(editedService, StringComparer.OrdinalIgnoreCase)))
                    {
                        names.Add(service.Metadata.Name);
                        names.Add(service.EffectivePublicName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveLayerServicesFailed(_logger, layerId, ex);
        }

        return [.. names];
    }

    private static string[] Names(MetadataV2Service service) => [service.Metadata.Id, service.Metadata.Name, service.EffectivePublicName];
}
