// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.MapServer;

/// <summary>
/// Resolves the deterministic, per-numeric-ID publication set shared by every synchronous and
/// durable MapServer execution path (legend, metadata, find, identify, KML, tile export). One
/// winner is chosen per service-local layer ID — Esri map, then Esri feature, then any other
/// adapter — so all MapServer surfaces describe the same layers the tile/render path draws.
/// </summary>
internal static class MapServerPublicationResolver
{
    internal static MetadataV2Publication[] ResolveLayerPublications(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var publications = new List<MetadataV2Publication>();
        var seenPublicLayerIds = new HashSet<int>();

        // Rank every candidate publication for the service so that a resource exposed
        // through several protocol adapters under the same service-local numeric ID
        // (automatic publishing emits, for example, an Esri feature layer plus a STAC
        // collection) resolves to a single deterministic winner: an explicit Esri map
        // publication first, then a valid Esri feature publication, then any remaining
        // adapter. We deliberately do NOT drop a layer whose only publication is
        // non-Esri: the MapServer render/tile path (ResolveTileLayerDescriptors)
        // enumerates this same publication set unfiltered, so the legend, metadata,
        // find, identify and KML surfaces must describe every layer the service
        // actually renders — otherwise a service whose tiles draw returns an empty
        // legend (honua-server#3046 regression). Dedupe by numeric ID keeps the
        // handler/worker dictionary lookups collision-free.
        var candidatePublications = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Where(snapshot.IsRoutable)
            .OrderBy(static publication => publication.PublicationType switch
            {
                MetadataV2PublicationType.EsriMapLayer => 0,
                MetadataV2PublicationType.EsriFeatureLayer => 1,
                _ => 2,
            })
            .ThenBy(static publication => publication.Metadata.Id, StringComparer.Ordinal);

        foreach (var publication in candidatePublications)
        {
            // Skip entries without a numeric public ID, dangling entries whose resource
            // is missing (they must never reserve an ID), and any adapter that would
            // duplicate an ID already claimed by a higher-ranked publication.
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
