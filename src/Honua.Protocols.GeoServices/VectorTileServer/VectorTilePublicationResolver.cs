// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;

namespace Honua.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Selects the deterministic primary publication for all VectorTileServer data
/// and style requests. Keeping this policy in one seam prevents root.json from
/// describing a different layer than tile/{z}/{y}/{x}.pbf (#4112).
/// </summary>
internal static class VectorTilePublicationResolver
{
    public static (MetadataV2Publication Publication, MetadataV2Resource Resource)? ResolvePrimary(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext? context = null)
    {
        (MetadataV2Publication Publication, MetadataV2Resource Resource)? best = null;
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (!snapshot.IsRoutable(publication) || resource is null ||
                (context is not null && !AccessPolicyHelpers.IsResourceAccessible(context, resource, service)))
            {
                continue;
            }

            if (best is null || IsPreferred(publication, best.Value.Publication))
            {
                best = (publication, resource);
            }
        }

        return best;
    }

    private static bool IsPreferred(MetadataV2Publication candidate, MetadataV2Publication current)
    {
        if (candidate.IsPrimary != current.IsPrimary) return candidate.IsPrimary;

        var candidateVector = candidate.PublicationType == MetadataV2PublicationType.EsriVectorTileLayer;
        var currentVector = current.PublicationType == MetadataV2PublicationType.EsriVectorTileLayer;
        if (candidateVector != currentVector) return candidateVector;

        var candidateIndex = candidate.LayerIndex ?? int.MaxValue;
        var currentIndex = current.LayerIndex ?? int.MaxValue;
        return candidateIndex != currentIndex
            ? candidateIndex < currentIndex
            : string.Compare(candidate.Metadata.Id, current.Metadata.Id, StringComparison.Ordinal) < 0;
    }
}
