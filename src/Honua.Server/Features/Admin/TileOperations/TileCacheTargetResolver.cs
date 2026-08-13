// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.TileOperations;

internal readonly record struct TileCacheTargetResolution(
    IReadOnlyList<int> LayerIds,
    IReadOnlyList<string> PublicationIds,
    bool IsServiceScoped);

/// <summary>
/// Resolves protocol-facing tile-operation targets to the publication layer indices embedded in
/// generated tile cache keys, falling back to the authoritative storage layer ID for publications
/// whose service-local identifier is not numeric.
/// </summary>
internal static class TileCacheTargetResolver
{
    public static async Task<IReadOnlyList<int>> ResolveLayerIdsAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
        => (await ResolveAsync(request, graphProvider, cancellationToken).ConfigureAwait(false)).LayerIds;

    public static async Task<TileCacheTargetResolution> ResolveAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return new TileCacheTargetResolution(
                request.LayerId.HasValue ? [request.LayerId.Value] : [],
                [],
                IsServiceScoped: false);
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshot.FindService(request.ServiceId);
        if (service is null)
        {
            return new TileCacheTargetResolution([], [], IsServiceScoped: true);
        }

        var publications = snapshot.PublicationsForService(service.Metadata.Id)
            .Select(publication => new
            {
                Publication = publication,
                CacheLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication)
            })
            .Where(static publication => publication.CacheLayerId.HasValue)
            .Where(publication =>
                !request.LayerId.HasValue || publication.CacheLayerId == request.LayerId.Value)
            .ToArray();
        var layerIds = publications
            .Select(static publication => publication.CacheLayerId!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var publicationIds = publications
            .Select(static publication => publication.Publication.Metadata.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new TileCacheTargetResolution(layerIds, publicationIds, IsServiceScoped: true);
    }
}
