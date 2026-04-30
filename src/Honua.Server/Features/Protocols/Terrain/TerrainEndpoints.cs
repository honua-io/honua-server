// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Raster;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Terrain;

internal static class TerrainEndpoints
{
    private const string JsonContentType = "application/json";
    private const string PngContentType = "image/png";

    public static IEndpointRouteBuilder MapTerrainEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/terrain/{datasetId}/tile.json", HandleGetMetadata)
            .WithDisplayName("Get Terrain TileJSON Metadata")
            .WithName("GetTerrainTileJson")
            .WithSummary("Get Terrain-RGB TileJSON metadata for a raster dataset")
            .WithDescription("Returns TileJSON 3.0 metadata and Honua terrain extensions for MapLibre/Mapbox raster-dem clients")
            .WithTags("Terrain")
            .CacheOutput("TerrainMetadata")
            .WithETag()
            .Produces<TerrainMetadataResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/terrain/{datasetId}/{z:int}/{x:int}/{y:int}.png", HandleGetTile)
            .WithDisplayName("Get Terrain-RGB Tile")
            .WithName("GetTerrainRgbTile")
            .WithSummary("Get a Terrain-RGB elevation tile")
            .WithDescription("Returns a 256x256 PNG tile encoded with the Mapbox Terrain-RGB formula")
            .WithTags("Terrain")
            .CacheOutput("TerrainTile")
            .Produces(StatusCodes.Status200OK, contentType: PngContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> HandleGetMetadata(
        string datasetId,
        HttpContext context,
        [FromServices] ITerrainTileService terrainTileService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] IOptions<TileOptions> tileOptions,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata);
        var metadata = await terrainTileService.GetDatasetMetadataAsync(
            layer.Id,
            mergeStrategy,
            cancellationToken);
        if (metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "No raster source is registered for this terrain dataset.");
        }

        var (minZoom, maxZoom) = ResolveZoomLimits(limitsOptions.Value.Tiles);
        var response = BuildMetadataResponse(datasetId, layer, metadata, minZoom, maxZoom, context);

        SetCacheHeader(context, tileOptions.Value.CacheMaxAge);
        return Results.Json(response, TerrainJsonContext.Default.TerrainMetadataResponse, contentType: JsonContentType);
    }

    private static async Task<IResult> HandleGetTile(
        string datasetId,
        int z,
        int x,
        int y,
        HttpContext context,
        [FromServices] ITerrainTileService terrainTileService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] IOptions<TileOptions> tileOptions,
        CancellationToken cancellationToken)
    {
        var tileValidation = ValidateTileCoordinates(context, z, x, y, limitsOptions.Value.Tiles);
        if (tileValidation != null)
        {
            return tileValidation;
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        using var activity = HonuaTelemetry.StartActivity(HonuaTelemetry.Activities.TileGeneration);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Terrain);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "terrain.tile");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);
        activity?.SetTag(HonuaTelemetry.Tags.TileZ, z);
        activity?.SetTag(HonuaTelemetry.Tags.TileX, x);
        activity?.SetTag(HonuaTelemetry.Tags.TileY, y);

        try
        {
            var tile = await terrainTileService.GetTerrainRgbTileAsync(
                new TerrainTileRequest
                {
                    LayerId = layer.Id,
                    Z = z,
                    X = x,
                    Y = y,
                    MergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata)
                },
                cancellationToken);

            activity?.SetTag("honua.raster.selected_count", tile.SelectedRasterCount);
            activity?.SetTag("honua.output.bytes", tile.Data.Length);
            activity?.SetTag("honua.terrain.all_nodata", tile.IsAllNoData);

            SetCacheHeader(context, tileOptions.Value.CacheMaxAge);
            return Results.Bytes(tile.Data, tile.ContentType);
        }
        catch (TerrainTileException ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, true);
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            return MapTerrainException(context, ex);
        }
    }

    private static Task<LayerValidationHelpers.LayerValidationResult> ValidateDatasetAsync(
        HttpContext context,
        string datasetId,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(datasetId, out var layerId))
        {
            return LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                AccessScope.Read,
                ServiceProtocols.Terrain,
                cancellationToken);
        }

        return LayerValidationHelpers.ValidateCollectionWithAccessAsync(
            context,
            datasetId,
            AccessScope.Read,
            ServiceProtocols.Terrain,
            cancellationToken);
    }

    private static IResult? ValidateTileCoordinates(HttpContext context, int z, int x, int y, TileLimits tileLimits)
    {
        var (minZoom, maxZoom) = ResolveZoomLimits(tileLimits);
        if (z < minZoom || z > maxZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile zoom level {z} is outside the configured range {minZoom}-{maxZoom}.");
        }

        if (!TileMath.ValidateTileCoordinates(x, y, z))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile coordinates z={z}, x={x}, y={y} are outside the WebMercator XYZ tile matrix bounds.");
        }

        _ = TileMath.GetTileBounds(x, y, z);
        return null;
    }

    private static (int MinZoom, int MaxZoom) ResolveZoomLimits(TileLimits tileLimits)
    {
        var minZoom = Math.Clamp(tileLimits.MinTileZoom, 0, TileMath.MaxSupportedZoomLevel);
        var maxZoom = Math.Clamp(tileLimits.MaxTileZoom, minZoom, TileMath.MaxSupportedZoomLevel);
        return (minZoom, maxZoom);
    }

    private static TerrainMetadataResponse BuildMetadataResponse(
        string datasetId,
        LayerDefinition layer,
        TerrainDatasetMetadata metadata,
        int minZoom,
        int maxZoom,
        HttpContext context)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var escapedDatasetId = Uri.EscapeDataString(datasetId);
        var tileUrl = $"{baseUrl}/terrain/{escapedDatasetId}/{{z}}/{{x}}/{{y}}.png";
        var bounds = metadata.Wgs84Bounds;
        var center = bounds == null
            ? null
            : new[] { (bounds[0] + bounds[2]) / 2.0, (bounds[1] + bounds[3]) / 2.0, (double)minZoom };

        return new TerrainMetadataResponse
        {
            Name = layer.Name,
            Description = layer.Description,
            Tiles = [tileUrl],
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = bounds,
            Center = center,
            Encoding = new TerrainEncodingMetadata(),
            Source = new TerrainSourceMetadata
            {
                DatasetId = datasetId,
                LayerId = layer.Id,
                RasterIds = metadata.RasterIds,
                RasterCount = metadata.RasterCount,
                SourceSrid = metadata.SourceSrid,
                SourceCrs = metadata.SourceSrid.HasValue ? $"EPSG:{metadata.SourceSrid.Value}" : null,
                SourceExtent = metadata.SourceExtent is { } extent
                    ? new TerrainExtentMetadata
                    {
                        XMin = extent.XMin,
                        YMin = extent.YMin,
                        XMax = extent.XMax,
                        YMax = extent.YMax,
                        Srid = extent.Srid
                    }
                    : null,
                PixelType = metadata.PixelType,
                BandCount = metadata.BandCount,
                VerticalUnit = metadata.VerticalUnit,
                VerticalDatum = metadata.VerticalDatum
            },
            NoData = new TerrainNoDataMetadata
            {
                SourceNoDataValue = metadata.SourceNoDataValue,
                TerrainRgbSentinelMeters = metadata.TerrainNoDataValue
            },
            Supported = metadata.IsSupported,
            UnsupportedReasons = metadata.UnsupportedReasons
        };
    }

    private static IResult MapTerrainException(HttpContext context, TerrainTileException exception)
        => exception.FailureKind switch
        {
            TerrainTileFailureKind.SourceUnavailable => StandardErrorHelpers.CreateNotFound(context, exception.Message),
            TerrainTileFailureKind.UnknownCrs or TerrainTileFailureKind.UnsupportedSource =>
                StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            _ => StandardErrorHelpers.CreateBadRequest(context, exception.Message)
        };

    private static void SetCacheHeader(HttpContext context, int cacheMaxAge)
        => context.Response.Headers["Cache-Control"] = $"public, max-age={Math.Max(0, cacheMaxAge)}";
}
