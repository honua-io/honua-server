// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.MapServer;

/// <summary>
/// Resolves the deterministic, protocol-specific publication set shared by synchronous and
/// durable MapServer execution paths.
/// </summary>
internal static class MapServerPublicationResolver
{
    internal static MetadataV2Publication[] ResolveLayerPublications(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var publications = new List<MetadataV2Publication>();
        var seenPublicLayerIds = new HashSet<int>();
        var mapPublications = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Where(static publication => publication.PublicationType is (
                MetadataV2PublicationType.EsriMapLayer or
                MetadataV2PublicationType.EsriFeatureLayer))
            .OrderBy(static publication =>
                publication.PublicationType == MetadataV2PublicationType.EsriMapLayer ? 0 : 1)
            .ThenBy(static publication => publication.Metadata.Id, StringComparer.Ordinal);

        foreach (var publication in mapPublications)
        {
            // Automatic publishing can expose one resource through several protocol adapters
            // with the same service-local numeric ID. Prefer an explicit Esri map publication,
            // fall back to a valid Esri feature publication, and never let dangling graph entries
            // reserve an ID or reach handler/worker dictionary lookups.
            if (publication.LayerIndex is not int publicLayerId ||
                snapshot.ResolveResource(publication) is null ||
                !seenPublicLayerIds.Add(publicLayerId))
            {
                continue;
            }

            publications.Add(publication);
        }

        return [.. publications];
    }
}
