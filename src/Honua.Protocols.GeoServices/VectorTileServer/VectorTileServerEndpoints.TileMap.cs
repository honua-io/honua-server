// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// FOUNDATION STUB (#1777). The tileMap (sparse tile index) route is declared here and
// stubbed as HTTP 501 so honua-server#1781 can implement the tileMap handler in this file
// WITHOUT editing the shared VectorTileServerEndpoints.cs. Replace HandleGetTileMap's body
// (and the route metadata as needed) when wiring the tileMap pipeline; do not move the
// registration.

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    /// <summary>
    /// Maps the VectorTileServer tileMap routes (sparse tile-availability index). The service
    /// descriptor advertises <c>tilemap</c> as its <c>tileMap</c> pointer. Stubbed as 501 in
    /// the foundation; implemented by honua-server#1781.
    /// </summary>
    private static void MapTileMapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/tilemap/{z:int}/{y:int}/{x:int}/{dimension:int}/{dimension2:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetTileMap(context))
            .WithDisplayName("Get Vector Tile Map")
            .WithName("GetVectorTileMap")
            .WithSummary("Get the VectorTileServer tile-availability map for a tile block")
            .WithDescription("Returns the sparse tile-availability index for a block of tiles. Stubbed (501) until honua-server#1781 wires the tileMap pipeline.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(501);
    }

    /// <summary>
    /// Foundation placeholder for the tileMap route. honua-server#1781 replaces this body with
    /// the real tile-availability handler.
    /// </summary>
    private static IResult HandleGetTileMap(HttpContext context)
        => Results.Problem(
            detail: "VectorTileServer tileMap is not yet implemented.",
            statusCode: StatusCodes.Status501NotImplemented);
}
