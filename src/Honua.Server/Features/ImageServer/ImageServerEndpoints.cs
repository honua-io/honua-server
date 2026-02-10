// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;

namespace Honua.Server.Features.ImageServer;

/// <summary>
/// Esri Image Server endpoints providing raster imagery capabilities.
/// Supports the Esri Image Server REST API specification.
/// </summary>
public static class ImageServerEndpoints
{
    /// <summary>
    /// Maps Image Server endpoints to the application.
    /// </summary>
    public static void MapImageServerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/rest/services/{id}/ImageServer")
            .WithTags("ImageServer");

        // Service metadata endpoint
        group.MapGet("", GetServiceInfo)
            .WithDisplayName("Get Image Service Info")
            .WithName("GetImageServiceInfo")
            .WithSummary("Get Image Server service metadata")
            .WithDescription("Returns comprehensive metadata about the image service including extent, capabilities, and raster properties")
            .Produces<ImageServerServiceInfo>()
            .Produces(404)
            .CacheOutput("ImageServerMetadata");

        // Export image endpoint - core rendering capability
        group.MapGet("/exportImage", ExportImage)
            .WithDisplayName("Export Image")
            .WithName("ExportImage")
            .WithSummary("Export rendered raster image")
            .WithDescription("Exports a rendered image from the raster dataset with optional clipping, resampling, and format conversion")
            .Produces<ExportImageResponse>()
            .Produces(400)
            .Produces(404);

        // Identify endpoint - pixel value query
        group.MapGet("/identify", Identify)
            .WithDisplayName("Identify Pixel Values")
            .WithName("Identify")
            .WithSummary("Get pixel values at a point")
            .WithDescription("Returns pixel values and metadata for all bands at the specified geographic location")
            .Produces<IdentifyResponse>()
            .Produces(400)
            .Produces(404);

        // Image tile endpoint - pre-tiled access
        group.MapGet("/tile/{level}/{row}/{col}", GetImageTile)
            .WithDisplayName("Get Image Tile")
            .WithName("GetImageTile")
            .WithSummary("Get pre-tiled image")
            .WithDescription("Returns a pre-generated image tile for efficient web mapping display")
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/jpeg")
            .Produces(200, contentType: "image/tiff")
            .Produces(204)
            .Produces(404);
    }

    /// <summary>
    /// Get Image Server service metadata.
    /// </summary>
    private static async Task<IResult> GetServiceInfo(
        int id,
        string? f,
        ImageServerMetadataHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (f != "json")
        {
            return Results.BadRequest("Only JSON format is supported. Use f=json");
        }

        return await handler.GetServiceInfoAsync(id, cancellationToken);
    }

    /// <summary>
    /// Export rendered image from raster data.
    /// </summary>
    private static async Task<IResult> ExportImage(
        int id,
        [AsParameters] ExportImageRequest request,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (request.F != "json")
        {
            return Results.BadRequest("Only JSON format is supported");
        }

        return await handler.ExportImageAsync(id, request, cancellationToken);
    }

    /// <summary>
    /// Identify pixel values at a geographic point.
    /// </summary>
    private static async Task<IResult> Identify(
        int id,
        [AsParameters] IdentifyRequest request,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (request.F != "json")
        {
            return Results.BadRequest("Only JSON format is supported");
        }

        return await handler.IdentifyAsync(id, request, cancellationToken);
    }

    /// <summary>
    /// Get pre-tiled image for efficient web mapping.
    /// </summary>
    private static async Task<IResult> GetImageTile(
        int id,
        int level,
        int row,
        int col,
        ImageServerTileHandler handler,
        string format = "png",
        CancellationToken cancellationToken = default)
    {
        return await handler.GetImageTileAsync(id, level, row, col, format, cancellationToken);
    }
}
