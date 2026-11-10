// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

internal static partial class StaticMapEndpoints
{
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
