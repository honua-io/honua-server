// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;

namespace Honua.Core.Features.Catalog;

public sealed class CatalogEndpointLinks(PublishedMetadataRequest request, IReadOnlyList<CatalogProtocolDescriptor> protocols)
{
    private Dictionary<string, string>? _mapCollectionIds;

    public IReadOnlyList<CatalogEndpoint> Resolve(IReadOnlyList<MetadataV2Publication> layers)
    {
        var links = new List<CatalogEndpoint>();
        foreach (var layer in layers)
        {
            var service = request.Snapshot.Index.ServicesById[layer.ServiceId];
            foreach (var endpoint in protocols.Where(endpoint => ServiceProtocols.IsProtocolEnabled(service, endpoint.Protocol)))
            {
                if (endpoint.PreferredLayerRequired && !ServiceProtocols.IsPreferredPublicationType(endpoint.Protocol, layer.PublicationType)) continue;
                if (CollectionId(request.Snapshot, layer, endpoint.Protocol) is not { } collectionId) continue;
                var path = endpoint.HrefTemplate
                    .Replace("{servicePath}", MetadataV2ServicePaths.GeoServicesPath(request.Snapshot.Graph, service), StringComparison.Ordinal)
                    .Replace("{serviceId}", Uri.EscapeDataString(service.Metadata.Name), StringComparison.Ordinal)
                    .Replace("{collectionId}", Uri.EscapeDataString(collectionId), StringComparison.Ordinal);
                if (path.Contains('{')) continue;
                links.Add(new(endpoint.Protocol, request.BaseUrl.TrimEnd('/') + path));
            }
        }
        return links.Distinct().ToArray();
    }

    private string? CollectionId(MetadataV2GraphSnapshot snapshot, MetadataV2Publication layer, string protocol)
    {
        if (protocol is ServiceProtocols.OgcFeatures or ServiceProtocols.OgcApiTiles or ServiceProtocols.OgcApiMaps or ServiceProtocols.Wfs20)
        {
            if (!snapshot.ResolveStorageLayerId(layer).HasValue) return null;
            return protocol == ServiceProtocols.OgcApiMaps
                ? (_mapCollectionIds ??= MetadataV2CollectionIds.ForMaps(snapshot)).GetValueOrDefault(layer.Metadata.Id)
                : MetadataV2CollectionIds.Advertised(layer, snapshot.ResolveResource(layer));
        }
        return protocol == ServiceProtocols.Stac && !layer.LayerIndex.HasValue ? null : layer.Metadata.Id;
    }
}
