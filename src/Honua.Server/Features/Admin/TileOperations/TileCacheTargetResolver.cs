// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Resolves protocol-facing tile-operation targets to the publication layer indices embedded in
/// generated tile cache keys.
/// </summary>
internal static class TileCacheTargetResolver
{
    public static async Task<IReadOnlyList<int>> ResolveLayerIdsAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        if (request.LayerId.HasValue)
        {
            return [request.LayerId.Value];
        }

        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return [];
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshot.FindService(request.ServiceId);
        if (service is null)
        {
            return [];
        }

        return snapshot.PublicationsForService(service.Metadata.Id)
            .Select(static publication => publication.LayerIndex)
            .Where(static layerId => layerId.HasValue)
            .Select(static layerId => layerId!.Value)
            .Distinct()
            .Order()
            .ToArray();
    }
}
