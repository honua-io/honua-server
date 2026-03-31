// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

/// <summary>
/// Maps static map image endpoints for URL-addressable image rendering.
/// Supports center+zoom and bbox-based image generation in png, jpeg, or webp.
/// </summary>
internal static partial class StaticMapEndpoints
{
    /// <summary>
    /// Maps static map image endpoints using AOT-compatible routing.
    /// </summary>
    public static IEndpointRouteBuilder MapStaticMapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Center + zoom: GET /static/{serviceId}/{lon},{lat},{zoom}/{width}x{height}.{format}
        endpoints.MapGet("/static/{serviceId}/{center}/{dimensions}.{format}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleCenterZoom(context))
            .WithDisplayName("Static Map (Center+Zoom)")
            .WithName("StaticMapCenterZoom")
            .WithSummary("Generate a static map image by center point and zoom level")
            .WithDescription("Renders a map image centered at lon,lat with a given zoom level, dimensions, and format")
            .WithTags("StaticMap");

        // Bbox: GET /static/{serviceId}/bbox/{bbox}/{width}x{height}.{format}
        endpoints.MapGet("/static/{serviceId}/bbox/{bbox}/{dimensions}.{format}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleBbox(context))
            .WithDisplayName("Static Map (Bbox)")
            .WithName("StaticMapBbox")
            .WithSummary("Generate a static map image by bounding box")
            .WithDescription("Renders a map image for the specified bounding box, dimensions, and format")
            .WithTags("StaticMap");

        return endpoints;
    }
}
