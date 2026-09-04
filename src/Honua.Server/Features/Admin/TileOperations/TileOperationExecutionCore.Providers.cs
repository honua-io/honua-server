// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles.Services;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed partial class TileOperationExecutionCore
{
    private sealed record TileSource(ITileProvider Provider, int StorageLayerId);

    private static async Task<IReadOnlyDictionary<int, TileSource>> ResolveTileSourcesAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var layerIds = await TileCacheTargetResolver.ResolveLayerIdsAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var fallback = services.GetRequiredService<ITileProvider>();
        var resolver = new TileFeatureProviderResolver(services.GetService<FeatureProviderQueryRouter>());
        var service = string.IsNullOrWhiteSpace(request.ServiceId) ? null : snapshot.FindService(request.ServiceId);
        IEnumerable<MetadataV2Publication> publications = service is null
            ? snapshot.Index.PublicationsById.Values
            : snapshot.PublicationsForService(service.Metadata.Id);
        var sources = new Dictionary<int, TileSource>();
        foreach (var layerId in layerIds)
        {
            // Job IDs are service-local cache targets, not physical storage handles. Resolve
            // each independently so service-wide seed/warm jobs may span several connections.
            var candidates = publications
                .Where(publication => (publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication)) == layerId)
                .ToArray();
            if (candidates.Length == 0)
            {
                if (service is not null)
                {
                    throw new InvalidOperationException("The tile operation target is not routable.");
                }

                sources.Add(layerId, new TileSource(fallback, layerId));
                continue;
            }

            // A bare numeric layer ID can collide across services. Never choose a foreign
            // binding by enumeration order; the caller must disambiguate with serviceId.
            var bindings = candidates.Select(publication => (
                    publication.ResourceId,
                    BindingId: snapshot.ResolveStorageBinding(publication)?.Metadata.Id))
                .Distinct().Count();
            if (bindings != 1)
            {
                throw new InvalidOperationException("The tile operation layer is ambiguous; specify a serviceId with an unambiguous layer binding.");
            }

            var publication = candidates[0];
            if (!snapshot.IsRoutable(publication))
            {
                throw new InvalidOperationException("The tile operation target is not routable.");
            }

            var resource = snapshot.ResolveResource(publication)!;
            var owningService = snapshot.Index.ServicesById[publication.ServiceId];
            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? layerId;
            var resolution = await resolver.ResolveTileProviderAsync(
                snapshot, owningService, resource, publication, storageLayerId, fallback, cancellationToken).ConfigureAwait(false);
            if (resolution.Provider is null)
            {
                throw new NotSupportedException("The configured data provider does not support vector tile generation for this layer.");
            }

            sources.Add(layerId, new TileSource(resolution.Provider, storageLayerId));
        }

        return sources;
    }
}
