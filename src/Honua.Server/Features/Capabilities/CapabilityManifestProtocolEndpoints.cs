// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Capabilities.Models;

namespace Honua.Server.Features.Capabilities;

internal static class CapabilityManifestProtocolEndpoints
{
    public static PublishedCatalogProtocol[] Build(CapabilityManifestCapability[] capabilities,
        CapabilityManifestDeploymentProfile profile, ManifestRoutedCapabilityIndex routes)
    {
        var endpoints = new List<PublishedCatalogProtocol>();
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.Add(new() { Protocol = ServiceProtocols.FeatureServer, HrefTemplate = "{servicePath}/FeatureServer" });
        endpoints.Add(new() { Protocol = ServiceProtocols.MapServer, HrefTemplate = "{servicePath}/MapServer" });
        endpoints.Add(new() { Protocol = ServiceProtocols.VectorTileServer, PreferredPublicationRequired = true, HrefTemplate = "{servicePath}/VectorTileServer" });
        if (Available(capabilities, "serve.geoservices-imageserver"))
        {
            endpoints.Add(new() { Protocol = ServiceProtocols.ImageServer, PreferredPublicationRequired = true, HrefTemplate = "{servicePath}/ImageServer" });
        }
#endif
#if !HONUA_EXCLUDE_OGC_API
        endpoints.Add(new() { Protocol = ServiceProtocols.OgcFeatures, HrefTemplate = "/ogc/features/collections/{collectionId}" });
        endpoints.Add(new() { Protocol = ServiceProtocols.OgcApiMaps, HrefTemplate = "/ogc/maps/collections/{collectionId}" });
        endpoints.Add(new() { Protocol = ServiceProtocols.OgcApiTiles, HrefTemplate = "/ogc/tiles/collections/{collectionId}" });
#endif
#if !HONUA_EXCLUDE_OGC_CLASSIC
        endpoints.Add(new() { Protocol = ServiceProtocols.Wms, HrefTemplate = "/ogc/services/{serviceId}/wms?SERVICE=WMS&REQUEST=GetCapabilities" });
        endpoints.Add(new() { Protocol = ServiceProtocols.Wfs20, HrefTemplate = "/wfs?SERVICE=WFS&REQUEST=GetCapabilities" });
        if (Available(capabilities, "serve.wmts"))
        {
            endpoints.Add(new() { Protocol = ServiceProtocols.Wmts, HrefTemplate = "/ogc/services/{serviceId}/wmts?SERVICE=WMTS&REQUEST=GetCapabilities" });
        }
#endif
#if !HONUA_EXCLUDE_STAC
        endpoints.Add(new() { Protocol = ServiceProtocols.Stac, PreferredPublicationRequired = true, HrefTemplate = "/stac/collections/{collectionId}" });
#endif
        return endpoints.Where(endpoint => IsEnabled(endpoint, profile, routes)).ToArray();
    }

    private static bool IsEnabled(PublishedCatalogProtocol endpoint, CapabilityManifestDeploymentProfile profile,
        ManifestRoutedCapabilityIndex routes)
    {
        if (!profile.Configured)
        {
            return true;
        }
        var path = endpoint.HrefTemplate.Split('?')[0].Replace("{servicePath}", "/rest/services/catalog", StringComparison.Ordinal);
        var capability = routes.CapabilityForPath(path);
        return capability is not null && profile.EnabledCapabilities.Contains(capability, StringComparer.Ordinal);
    }

    private static bool Available(IEnumerable<CapabilityManifestCapability> capabilities, string id)
        => capabilities.Any(capability => capability.Id == id && capability.Supported && capability.Available);
}
