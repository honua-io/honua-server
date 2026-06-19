// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// FOUNDATION STUB (#1777). The vector tile style / resources routes are declared here and
// stubbed as HTTP 501 so honua-server#1779 can implement the resource handlers in this file
// WITHOUT editing the shared VectorTileServerEndpoints.cs. Replace the handler bodies (and
// the route metadata as needed) when wiring the styles pipeline; do not move the registration.

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    /// <summary>
    /// Maps the VectorTileServer resources routes (default styles, sprites, glyphs). The
    /// service descriptor advertises <c>resources/styles</c> as <c>defaultStyles</c>.
    /// Stubbed as 501 in the foundation; implemented by honua-server#1779.
    /// </summary>
    private static void MapResourcesEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetDefaultStyles(context))
            .WithDisplayName("Get Vector Tile Default Styles")
            .WithName("GetVectorTileDefaultStyles")
            .WithSummary("Get the default vector tile style document (root.json)")
            .WithDescription("Returns the Mapbox GL style JSON for the service. Stubbed (501) until honua-server#1779 wires the styles pipeline.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(501);

        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetStyleResource(context))
            .WithDisplayName("Get Vector Tile Style Resource")
            .WithName("GetVectorTileStyleResource")
            .WithSummary("Get a vector tile style sub-resource (sprites, glyphs, root.json)")
            .WithDescription("Returns a style sub-resource. Stubbed (501) until honua-server#1779 wires the styles pipeline.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(404)
            .Produces(501);
    }

    /// <summary>
    /// Foundation placeholder for the default styles resource. honua-server#1779 replaces this
    /// body with the real style document handler.
    /// </summary>
    private static IResult HandleGetDefaultStyles(HttpContext context)
        => Results.Problem(
            detail: "VectorTileServer default styles are not yet implemented.",
            statusCode: StatusCodes.Status501NotImplemented);

    /// <summary>
    /// Foundation placeholder for style sub-resources. honua-server#1779 replaces this body
    /// with the real style sub-resource handler.
    /// </summary>
    private static IResult HandleGetStyleResource(HttpContext context)
        => Results.Problem(
            detail: "VectorTileServer style resources are not yet implemented.",
            statusCode: StatusCodes.Status501NotImplemented);
}
