// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Classic.Wmts;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// Maps classic OGC web service endpoints.
/// </summary>
internal static class OgcClassicEndpoints
{
    /// <summary>
    /// Maps WMS and WMTS endpoints, including GeoServices compatibility aliases.
    /// </summary>
    public static IEndpointRouteBuilder MapOgcClassicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/WMTS",
                static (HttpContext context, CancellationToken cancellationToken) => WmtsRequestHandlers.HandleWmts(context))
            .WithDisplayName("WMTS Service")
            .WithName("MapServerWmts")
            .WithSummary("OGC WMTS endpoint")
            .WithDescription("Provides OGC WMTS GetCapabilities, GetTile, and GetFeatureInfo operations")
            .WithTags("WMTS", "OGC", "MapServer")
            .CacheOutput(policy => policy.NoCache());

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/WMTS/{**restPath}",
                static (HttpContext context, CancellationToken cancellationToken) => WmtsRequestHandlers.HandleWmtsRestful(context))
            .WithDisplayName("WMTS RESTful Resource")
            .WithName("MapServerWmtsRestful")
            .WithSummary("OGC WMTS RESTful endpoint")
            .WithDescription("Provides OGC WMTS RESTful resources for capabilities, tiles, and feature info")
            .WithTags("WMTS", "OGC", "MapServer")
            .CacheOutput(policy => policy.NoCache());

        endpoints.MapGet("/ogc/services/{serviceId}/wmts",
                static (HttpContext context, CancellationToken cancellationToken) => WmtsRequestHandlers.HandleWmts(context))
            .WithDisplayName("WMTS Service (OGC Alias)")
            .WithName("OgcServiceWmts")
            .WithSummary("OGC WMTS endpoint (service-scoped alias)")
            .WithDescription("Service-scoped OGC WMTS endpoint")
            .WithTags("WMTS", "OGC")
            .CacheOutput(policy => policy.NoCache());

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/WMS",
                static (HttpContext context, CancellationToken cancellationToken) => WmsRequestHandlers.HandleWms(context))
            .WithDisplayName("WMS Service")
            .WithName("MapServerWms")
            .WithSummary("OGC WMS endpoint")
            .WithDescription("Provides OGC WMS GetCapabilities, GetMap, and GetFeatureInfo operations")
            .WithTags("WMS", "OGC", "MapServer");

        endpoints.MapGet("/ogc/services/{serviceId}/wms",
                static (HttpContext context, CancellationToken cancellationToken) => WmsRequestHandlers.HandleWms(context))
            .WithDisplayName("WMS Service (OGC Alias)")
            .WithName("OgcServiceWms")
            .WithSummary("OGC WMS endpoint (service-scoped alias)")
            .WithDescription("Service-scoped OGC WMS endpoint")
            .WithTags("WMS", "OGC");

        return endpoints;
    }
}
