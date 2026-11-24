// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Raster;
using Honua.Infrastructure.SharedCaching;
using Honua.Infrastructure.Validation;
#if !HONUA_EXCLUDE_SCENE
using Honua.Server.Features.Protocols.Terrain.QuantizedMesh;
#endif
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Terrain;

internal static class TerrainEndpoints
{
    internal const string JsonContentType = "application/json";
    private const string PngContentType = "image/png";
    private const string TerrainProtocolName = "Terrain";
    private const string TerrainRgbCapabilityKey = "raster.terrain-rgb";
    private const string QuantizedMeshCapabilityKey = "serve.terrain-quantized-mesh";

    public static IEndpointRouteBuilder MapTerrainEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/terrain/{datasetId}/tile.json", HandleGetMetadata)
            .WithDisplayName("Get Terrain TileJSON Metadata")
            .WithName("GetTerrainTileJson")
            .WithSummary("Get Terrain-RGB TileJSON metadata for a raster dataset")
            .WithDescription("Returns TileJSON 3.0 metadata and Honua terrain extensions for MapLibre/Mapbox raster-dem clients")
            .WithTags("Terrain")
            .CacheOutput("TerrainMetadata")
            .WithSharedCacheClass(ResponseCacheClass.MetadataDocument)
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
            .WithSharedCacheClass(ResponseCacheClass.Tile)
            .Produces(StatusCodes.Status200OK, contentType: PngContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

#if !HONUA_EXCLUDE_SCENE
        endpoints.MapQuantizedMeshTerrainEndpoints();
#endif

        return endpoints;
    }

    private static async Task<IResult> HandleGetMetadata(
        string datasetId,
        HttpContext context,
        [FromServices] ITerrainTileService terrainTileService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.StorageLayerId;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource);
        var metadata = await terrainTileService.GetDatasetMetadataAsync(
            layerId,
            mergeStrategy,
            cancellationToken);
        if (metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "No raster source is registered for this terrain dataset.");
        }

        var (minZoom, maxZoom) = ResolveZoomLimits(limitsOptions.Value.Tiles);
        var response = BuildMetadataResponse(datasetId, resource, layerId, metadata, minZoom, maxZoom, context);

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

        var resource = validation.Resource!;
        var layerId = validation.StorageLayerId;
        using var activity = HonuaTelemetry.StartActivity(HonuaTelemetry.Activities.TileGeneration);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Terrain);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "terrain.tile");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);
        activity?.SetTag(HonuaTelemetry.Tags.TileZ, z);
        activity?.SetTag(HonuaTelemetry.Tags.TileX, x);
        activity?.SetTag(HonuaTelemetry.Tags.TileY, y);

        try
        {
            var tile = await terrainTileService.GetTerrainRgbTileAsync(
                new TerrainTileRequest
                {
                    LayerId = layerId,
                    Z = z,
                    X = x,
                    Y = y,
                    MergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource)
                },
                cancellationToken);

            activity?.SetTag("honua.raster.selected_count", tile.SelectedRasterCount);
            activity?.SetTag("honua.output.bytes", tile.Data.Length);
            activity?.SetTag("honua.terrain.all_nodata", tile.IsAllNoData);

            return Results.Bytes(tile.Data, tile.ContentType);
        }
        catch (TerrainTileException ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, true);
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            return MapTerrainException(context, ex);
        }
    }

    internal readonly record struct ValidatedTerrainDataset(
        bool IsValid,
        int StorageLayerId,
        MetadataV2Object? Resource,
        IResult? ErrorResult);

    internal static async Task<ValidatedTerrainDataset> ValidateDatasetAsync(
        HttpContext context,
        string datasetId,
        CancellationToken cancellationToken)
    {
        var validation = int.TryParse(datasetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId)
            ? await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
                context,
                layerId,
                AccessScope.Read,
                TerrainProtocolName,
                cancellationToken).ConfigureAwait(false)
            : await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context,
                datasetId,
                AccessScope.Read,
                TerrainProtocolName,
                cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            return new ValidatedTerrainDataset(false, 0, null, validation.ErrorResult);
        }

        var storageLayerId = validation.Snapshot is not null && validation.Publication is not null
            ? validation.Snapshot.ResolveStorageLayerId(validation.Publication, validation.Resource)
            : validation.Publication?.LayerIndex;
        if (!storageLayerId.HasValue)
        {
            return new ValidatedTerrainDataset(
                false,
                0,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Dataset '{datasetId}' not found."));
        }

        if (!RequestAnonymity.IsAnonymous(context))
        {
            context.Response.Headers.Append(HeaderNames.Vary, "Authorization, X-API-Key");
        }

        return new ValidatedTerrainDataset(true, storageLayerId.Value, validation.Resource, null);
    }

    internal static (int MinZoom, int MaxZoom) ResolveZoomLimits(TileLimits tileLimits)
    {
        var minZoom = Math.Clamp(tileLimits.MinTileZoom, 0, TileMath.MaxSupportedZoomLevel);
        var maxZoom = Math.Clamp(tileLimits.MaxTileZoom, minZoom, TileMath.MaxSupportedZoomLevel);
        return (minZoom, maxZoom);
    }

    internal static TerrainSourceMetadata BuildSourceMetadata(string datasetId, int layerId, TerrainDatasetMetadata metadata)
    {
        var sourceSrid = metadata.SourceSrid is > 0 ? metadata.SourceSrid.Value : (int?)null;
        return new TerrainSourceMetadata
        {
            DatasetId = datasetId,
            LayerId = layerId,
            RasterIds = metadata.RasterIds,
            RasterCount = metadata.RasterCount,
            SourceSrid = sourceSrid,
            SourceCrs = sourceSrid.HasValue ? $"EPSG:{sourceSrid.Value}" : null,
            SourceExtent = sourceSrid.HasValue && metadata.SourceExtent is { } extent
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
        };
    }

    internal static TerrainCoverageMetadata BuildCoverage(TerrainDatasetMetadata metadata, int minZoom, int maxZoom)
        => new()
        {
            Wgs84Bounds = metadata.Wgs84Bounds,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            RasterCount = metadata.RasterCount,
            HasCoverage = metadata.IsSupported && metadata.Wgs84Bounds is { Length: 4 }
        };

    internal static TerrainCapabilitiesMetadata BuildCapabilities(string baseUrl, string escapedDatasetId, TerrainDatasetMetadata metadata)
    {
        var tileJsonUrl = $"{baseUrl}/terrain/{escapedDatasetId}/tile.json";
        var layerJsonUrl = $"{baseUrl}/terrain/{escapedDatasetId}/layer.json";
        var reasons = metadata.IsSupported ? [] : metadata.UnsupportedReasons;

        var terrainRgb = new TerrainCapabilityStatus
        {
            Capability = TerrainRgbCapabilityKey,
            Format = "terrain-rgb",
            Available = metadata.IsSupported,
            Status = metadata.IsSupported ? "available" : "unavailable",
            Href = tileJsonUrl,
            Reasons = reasons
        };

#if !HONUA_EXCLUDE_SCENE
        var quantizedMesh = new TerrainCapabilityStatus
        {
            Capability = QuantizedMeshCapabilityKey,
            Format = "quantized-mesh-1.0",
            Available = metadata.IsSupported,
            Status = metadata.IsSupported ? "available" : "unavailable",
            Href = layerJsonUrl,
            Reasons = reasons
        };
#else
        var quantizedMesh = new TerrainCapabilityStatus
        {
            Capability = QuantizedMeshCapabilityKey,
            Format = "quantized-mesh-1.0",
            Available = false,
            Status = "unavailable",
            Href = null,
            Reasons = ["Quantized-mesh terrain is excluded by the active server protocol profile."]
        };
#endif

        return new TerrainCapabilitiesMetadata
        {
            TerrainRgb = terrainRgb,
            QuantizedMesh = quantizedMesh
        };
    }

    internal static TerrainLinkMetadata[] BuildLinks(string baseUrl, string escapedDatasetId, bool selfIsQuantizedMesh)
    {
        var tileJsonLink = new TerrainLinkMetadata
        {
            Rel = selfIsQuantizedMesh ? "alternate" : "self",
            Href = $"{baseUrl}/terrain/{escapedDatasetId}/tile.json",
            Type = JsonContentType,
            Format = "terrain-rgb",
            Title = "Terrain-RGB TileJSON metadata"
        };

#if !HONUA_EXCLUDE_SCENE
        var layerJsonLink = new TerrainLinkMetadata
        {
            Rel = selfIsQuantizedMesh ? "self" : "alternate",
            Href = $"{baseUrl}/terrain/{escapedDatasetId}/layer.json",
            Type = JsonContentType,
            Format = "quantized-mesh-1.0",
            Title = "Cesium quantized-mesh-1.0 layer.json metadata"
        };
        return selfIsQuantizedMesh ? [layerJsonLink, tileJsonLink] : [tileJsonLink, layerJsonLink];
#else
        return [tileJsonLink];
#endif
    }

    internal static IResult MapTerrainException(HttpContext context, TerrainTileException exception)
        => exception.FailureKind switch
        {
            TerrainTileFailureKind.SourceUnavailable => StandardErrorHelpers.CreateNotFound(context, exception.Message),
            TerrainTileFailureKind.UnknownCrs or TerrainTileFailureKind.UnsupportedSource =>
                StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            _ => StandardErrorHelpers.CreateBadRequest(context, exception.Message)
        };

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

    private static TerrainMetadataResponse BuildMetadataResponse(
        string datasetId,
        MetadataV2Object resource,
        int layerId,
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
            Name = resource.Metadata.Title ?? resource.Metadata.Name,
            Description = resource.Metadata.Description,
            Attribution = resource.Metadata.Attribution,
            Tiles = [tileUrl],
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = bounds,
            Center = center,
            Encoding = new TerrainEncodingMetadata(),
            Source = BuildSourceMetadata(datasetId, layerId, metadata),
            NoData = new TerrainNoDataMetadata
            {
                SourceNoDataValue = metadata.SourceNoDataValue,
                TerrainRgbSentinelMeters = metadata.TerrainNoDataValue
            },
            Coverage = BuildCoverage(metadata, minZoom, maxZoom),
            Capabilities = BuildCapabilities(baseUrl, escapedDatasetId, metadata),
            Links = BuildLinks(baseUrl, escapedDatasetId, selfIsQuantizedMesh: false),
            Supported = metadata.IsSupported,
            UnsupportedReasons = metadata.UnsupportedReasons
        };
    }
}
