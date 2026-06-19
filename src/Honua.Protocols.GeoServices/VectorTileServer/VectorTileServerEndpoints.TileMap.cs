// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// honua-server#1781: VectorTileServer tileMap (sparse tile-availability index). The service
// descriptor advertises "tilemap" as its tileMap pointer; ArcGIS clients call this route to
// learn which tiles in a block exist before requesting them. This file fills the foundation
// 501 stub (#1777) WITHOUT editing the shared VectorTileServerEndpoints.cs; the route
// registration stays here.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.VectorTileServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    /// <summary>
    /// Largest tile-block edge (in tiles) a single tileMap request may ask for. ArcGIS
    /// clients probe small blocks (typically 8x8); bound the request so an absurd dimension
    /// cannot allocate an unbounded availability buffer.
    /// </summary>
    private const int MaxTileMapDimension = 32;

    /// <summary>
    /// Maps the VectorTileServer tileMap routes (sparse tile-availability index). The service
    /// descriptor advertises <c>tilemap</c> as its <c>tileMap</c> pointer. Implemented by
    /// honua-server#1781: returns the Esri tileMap availability block computed from the
    /// service tiling scheme (LOD range + WebMercatorQuad coordinate bounds).
    /// </summary>
    private static void MapTileMapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/tilemap/{z:int}/{y:int}/{x:int}/{dimension:int}/{dimension2:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetTileMap(context))
            .WithDisplayName("Get Vector Tile Map")
            .WithName("GetVectorTileMap")
            .WithSummary("Get the VectorTileServer tile-availability map for a tile block")
            .WithDescription("Returns the Esri tile-availability index for a block of tiles: 1 for tiles inside the LOD/coordinate bounds, 0 for tiles outside them.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: JsonContentType)
            .Produces(400)
            .Produces(404);

        // The tileMap root resource. ArcGIS clients sometimes GET the bare `tilemap` pointer
        // advertised by the service descriptor before walking the {z}/{y}/{x}/{dim}/{dim}
        // blocks; return the top-of-pyramid availability block so the resource resolves.
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/tilemap",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetTileMapRoot(context))
            .WithDisplayName("Get Vector Tile Map Root")
            .WithName("GetVectorTileMapRoot")
            .WithSummary("Get the VectorTileServer root tile-availability map")
            .WithDescription("Returns the Esri tile-availability index for the top of the tiling pyramid (level 0).")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: JsonContentType)
            .Produces(404);
    }

    /// <summary>
    /// Handles the tileMap root resource (<c>tilemap</c>) by returning the level-0
    /// availability block (a single tile that always exists).
    /// </summary>
    private static Task<IResult> HandleGetTileMapRoot(HttpContext context)
        => HandleTileMapAsync(context, z: 0, y: 0, x: 0, height: 1, width: 1);

    /// <summary>
    /// Handles a tileMap block request (<c>tilemap/{z}/{y}/{x}/{dim}/{dim}</c>). The route
    /// segments are <c>z</c> (LOD), <c>y</c>/<c>x</c> (top-left tile of the block), and two
    /// dimensions (block height then width, in tiles, per the Esri tileMap convention).
    /// </summary>
    private static Task<IResult> HandleGetTileMap(HttpContext context)
    {
        var z = GetRouteInt(context, "z");
        var y = GetRouteInt(context, "y");
        var x = GetRouteInt(context, "x");
        var height = GetRouteInt(context, "dimension");
        var width = GetRouteInt(context, "dimension2");
        return HandleTileMapAsync(context, z, y, x, height, width);
    }

    private static async Task<IResult> HandleTileMapAsync(HttpContext context, int z, int y, int x, int height, int width)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        // Bound the block dimensions before touching the catalog so an absurd request is
        // rejected cheaply. The top-left tile coordinates may legitimately be large at deep
        // zooms, so they are range-checked against the LOD grid rather than rejected here.
        if (height <= 0 || width <= 0 || height > MaxTileMapDimension || width > MaxTileMapDimension)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"tileMap block dimensions must be between 1 and {MaxTileMapDimension} tiles per side.");
        }

        if (z < 0 || x < 0 || y < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "tileMap tile coordinates must be non-negative.");
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "tilemap",
            HonuaTelemetry.Protocols.VectorTileServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-tilemap");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                MetadataV2ServiceProtocols.VectorTileServer,
                context,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var maxLod = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles.MaxTileZoom;

            // A tileMap request whose LOD lies outside the service tiling scheme cannot be
            // satisfied — Esri returns an error rather than an all-zero block. Reject it as a
            // bad request so clients distinguish "level not served" from "tiles not present".
            if (z > maxLod)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Requested level {z} is outside the service tiling scheme (0..{maxLod}).");
            }

            var response = BuildTileMapResponse(z, y, x, height, width, maxLod);

            stopwatch.Stop();
            scope.SetSuccess(response.Data.Length);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, VectorTileServerJsonContext.Default.VectorTileMapResponse, contentType: JsonContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Computes the dense tileMap availability block: <c>1</c> for every tile inside the
    /// LOD/coordinate bounds of the WebMercatorQuad tiling scheme, <c>0</c> for tiles that
    /// fall outside the level's coordinate grid. The block is row-major over
    /// <paramref name="height"/> rows then <paramref name="width"/> columns, starting at the
    /// top-left tile (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    private static VectorTileMapResponse BuildTileMapResponse(int z, int y, int x, int height, int width, int maxLod)
    {
        var data = new int[height * width];
        var index = 0;
        for (var row = 0; row < height; row++)
        {
            var tileY = y + row;
            for (var col = 0; col < width; col++)
            {
                var tileX = x + col;
                // In-range tiles within the served LOD range map to 1; tiles that overrun the
                // level's grid (e.g. the right/bottom edge of the world at this zoom) map to 0.
                var available = z <= maxLod && TileMath.ValidateTileCoordinates(tileX, tileY, z);
                data[index++] = available ? 1 : 0;
            }
        }

        return new VectorTileMapResponse
        {
            // The availability block is returned exactly as requested (no clamping), so the
            // location mirrors the request and `adjusted` is false.
            Adjusted = false,
            Location = new VectorTileMapLocation
            {
                Left = x,
                Top = y,
                Width = width,
                Height = height
            },
            Data = data
        };
    }

    private static int GetRouteInt(HttpContext context, string key)
        => context.Request.RouteValues.TryGetValue(key, out var value)
           && value is not null
           && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
}
