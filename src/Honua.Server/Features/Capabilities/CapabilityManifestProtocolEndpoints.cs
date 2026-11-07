// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Hosting;
using Honua.Server.Features.Capabilities.Models;

namespace Honua.Server.Features.Capabilities;

internal static class CapabilityManifestProtocolEndpoints
{
    private sealed record ProtocolRoute(string Protocol, string RouteId)
    {
        public string Query { get; init; } = "";
        public bool PreferredPublicationRequired { get; init; }
    }

    private static readonly ProtocolRoute[] Routes =
    [
        new(ServiceProtocols.FeatureServer, "get-rest-services-serviceid-featureserver"),
        new(ServiceProtocols.MapServer, "get-rest-services-serviceid-mapserver"),
        new(ServiceProtocols.VectorTileServer, "get-rest-services-serviceid-vectortileserver") { PreferredPublicationRequired = true },
        new(ServiceProtocols.ImageServer, "get-rest-services-serviceid-imageserver") { PreferredPublicationRequired = true },
        new(ServiceProtocols.OgcFeatures, "get-ogc-features-collections-collectionid"),
        new(ServiceProtocols.OgcApiMaps, "get-ogc-maps-collections-collectionid"),
        new(ServiceProtocols.OgcApiTiles, "get-ogc-tiles-collections-collectionid"),
        new(ServiceProtocols.Wms, "get-ogc-services-serviceid-wms") { Query = "?SERVICE=WMS&REQUEST=GetCapabilities" },
        new(ServiceProtocols.Wfs20, "get-wfs") { Query = "?SERVICE=WFS&REQUEST=GetCapabilities" },
        new(ServiceProtocols.Wmts, "get-ogc-services-serviceid-wmts") { Query = "?SERVICE=WMTS&REQUEST=GetCapabilities" },
        new(ServiceProtocols.Stac, "get-stac-collections-collectionid") { PreferredPublicationRequired = true }
    ];

    public static string[] CapabilityIds(ManifestRoutedCapabilityIndex routes)
        => Routes.Select(endpoint => routes.GetRoute(endpoint.RouteId)?.Capability).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    public static PublishedCatalogProtocol[] Build(CapabilityManifestCapability[] capabilities,
        CapabilityManifestDeploymentProfile profile, ManifestRoutedCapabilityIndex routes)
    {
        var endpoints = new List<PublishedCatalogProtocol>();
        foreach (var endpoint in Routes)
        {
            if (routes.GetRoute(endpoint.RouteId) is not { } route ||
                !ProtocolProfile.IsCapabilityCompiled(route.Capability) ||
                profile.Configured && !profile.EnabledCapabilities.Contains(route.Capability, StringComparer.Ordinal) ||
                !capabilities.Any(capability => capability.Id == route.Capability && capability.Supported && capability.Available))
            {
                continue;
            }
            endpoints.Add(new()
            {
                Protocol = endpoint.Protocol, PreferredPublicationRequired = endpoint.PreferredPublicationRequired,
                HrefTemplate = route.Path.Replace("/rest/services/{serviceId}", "{servicePath}", StringComparison.Ordinal) + endpoint.Query
            });
        }
        return endpoints.ToArray();
    }
}
