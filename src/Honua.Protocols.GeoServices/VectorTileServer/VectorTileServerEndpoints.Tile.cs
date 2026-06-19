// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// FOUNDATION STUB (#1777). The vector tile data route is declared here and stubbed as
// HTTP 501 so honua-server#1778 can implement the tile handler in this file WITHOUT
// editing the shared VectorTileServerEndpoints.cs. Replace HandleGetTile's body (and the
// route metadata as needed) when wiring the tile pipeline; do not move the registration.

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    /// <summary>
    /// Maps the VectorTileServer vector tile data route (<c>tile/{z}/{y}/{x}.pbf</c>).
    /// Stubbed as 501 in the foundation; implemented by honua-server#1778.
    /// </summary>
    private static void MapTileEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/tile/{z:int}/{y:int}/{x:int}.pbf",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetTile(context))
            .WithDisplayName("Get Vector Tile")
            .WithName("GetVectorTile")
            .WithSummary("Get a Mapbox Vector Tile (.pbf) from a VectorTileServer service")
            .WithDescription("Returns a protobuf-encoded vector tile. Stubbed (501) until honua-server#1778 wires the tile pipeline.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: "application/vnd.mapbox-vector-tile")
            .Produces(404)
            .Produces(501);
    }

    /// <summary>
    /// Foundation placeholder for the vector tile data route. honua-server#1778 replaces this
    /// body with the real tile handler.
    /// </summary>
    private static IResult HandleGetTile(HttpContext context)
        => Results.Problem(
            detail: "VectorTileServer tile retrieval is not yet implemented.",
            statusCode: StatusCodes.Status501NotImplemented);
}
