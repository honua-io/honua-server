// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Raster;
using Honua.Infrastructure.SharedCaching;
using Honua.Scene.Terrain;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Terrain.QuantizedMesh;

internal static class QuantizedMeshTerrainEndpoints
{
    private const string GzipEncoding = "gzip";
    private const string NormalsContentType = "application/vnd.quantized-mesh; extensions=octvertexnormals";

    public static IEndpointRouteBuilder MapQuantizedMeshTerrainEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/terrain/{datasetId}/layer.json", HandleGetLayerJson)
            .WithDisplayName("Get Quantized-Mesh Terrain Layer Metadata")
            .WithName("GetTerrainLayerJson")
            .WithSummary("Get Cesium quantized-mesh-1.0 layer.json metadata for a raster dataset")
            .WithDescription("Returns Cesium quantized-mesh-1.0 layer.json metadata with TMS geographic tile ranges and cross-format links")
            .WithTags("Terrain")
            .CacheOutput("TerrainMetadata")
            .WithSharedCacheClass(ResponseCacheClass.MetadataDocument)
            .WithETag()
            .Produces<QuantizedMeshLayerMetadataResponse>(StatusCodes.Status200OK, TerrainEndpoints.JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/terrain/{datasetId}/{z:int}/{x:int}/{y:int}.terrain", HandleGetTerrainTile)
            .WithDisplayName("Get Quantized-Mesh Terrain Tile")
            .WithName("GetTerrainQuantizedMeshTile")
            .WithSummary("Get a Cesium quantized-mesh-1.0 elevation tile")
            .WithDescription("Returns a gzipped quantized-mesh-1.0 binary tile with optional OctEncodedVertexNormals extension")
            .WithTags("Terrain")
            .CacheOutput("TerrainQuantizedMeshTile")
            .WithSharedCacheClass(ResponseCacheClass.Tile)
            .Produces(StatusCodes.Status200OK, contentType: QuantizedMeshTileWriter.MediaType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> HandleGetLayerJson(
        string datasetId,
        HttpContext context,
        [FromServices] ITerrainTileService terrainTileService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var validation = await TerrainEndpoints.ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.StorageLayerId;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource);
        var metadata = await terrainTileService.GetDatasetMetadataAsync(layerId, mergeStrategy, cancellationToken);
        if (metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "No raster source is registered for this terrain dataset.");
        }

        var (minZoom, maxZoom) = TerrainEndpoints.ResolveZoomLimits(limitsOptions.Value.Tiles);
        var response = BuildLayerJsonResponse(datasetId, resource, layerId, metadata, minZoom, maxZoom, context);

        return Results.Json(
            response,
            QuantizedMeshJsonContext.Default.QuantizedMeshLayerMetadataResponse,
            contentType: TerrainEndpoints.JsonContentType);
    }

    private static async Task<IResult> HandleGetTerrainTile(
        string datasetId,
        int z,
        int x,
        int y,
        HttpContext context,
        [FromServices] ITerrainTileService terrainTileService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var coordError = ValidateGeographicTileCoordinates(context, z, x, y, limitsOptions.Value.Tiles);
        if (coordError is not null)
        {
            return coordError;
        }

        var validation = await TerrainEndpoints.ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.StorageLayerId;
        var includeNormals = RequestsOctVertexNormals(context.Request);

        using var activity = HonuaTelemetry.StartActivity(HonuaTelemetry.Activities.TileGeneration);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Terrain);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "terrain.quantized-mesh");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);
        activity?.SetTag(HonuaTelemetry.Tags.TileZ, z);
        activity?.SetTag(HonuaTelemetry.Tags.TileX, x);
        activity?.SetTag(HonuaTelemetry.Tags.TileY, y);

        try
        {
            var grid = await terrainTileService.GetGeographicElevationGridAsync(
                new TerrainGeographicGridRequest
                {
                    LayerId = layerId,
                    Z = z,
                    X = x,
                    Y = y,
                    MergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource)
                },
                cancellationToken);

            var payload = QuantizedMeshTileWriter.WriteGzipped(new QuantizedMeshTileInput
            {
                Bounds = grid.Bounds,
                GridSize = grid.GridSize,
                Elevations = grid.Elevations,
                IncludeVertexNormals = includeNormals
            });

            activity?.SetTag("honua.raster.selected_count", grid.SelectedRasterCount);
            activity?.SetTag("honua.output.bytes", payload.Length);
            activity?.SetTag("honua.terrain.all_nodata", grid.IsAllNoData);

            context.Response.Headers.ContentEncoding = GzipEncoding;
            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
            var contentType = includeNormals ? NormalsContentType : QuantizedMeshTileWriter.MediaType;
            return Results.Bytes(payload, contentType);
        }
        catch (TerrainTileException ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, true);
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            return TerrainEndpoints.MapTerrainException(context, ex);
        }
    }

    internal static bool RequestsOctVertexNormals(HttpRequest request)
    {
        if (ContainsExtensionToken(request.Query["extensions"].ToString()))
        {
            return true;
        }

        if (!MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var acceptedMediaTypes))
        {
            return false;
        }

        foreach (var mediaType in acceptedMediaTypes)
        {
            if (mediaType.Quality is 0 ||
                !string.Equals(mediaType.MediaType.Value, QuantizedMeshTileWriter.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extensions = NameValueHeaderValue.Find(mediaType.Parameters, "extensions");
            if (extensions?.Value.Value is { } extensionValue && ContainsExtensionToken(extensionValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExtensionToken(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var tokens = rawValue.Trim().Trim('"').Split(['-', ',', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (string.Equals(token, QuantizedMeshTileWriter.OctVertexNormalsExtensionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IResult? ValidateGeographicTileCoordinates(
        HttpContext context,
        int z,
        int x,
        int y,
        TileLimits tileLimits)
    {
        var (minZoom, maxZoom) = TerrainEndpoints.ResolveZoomLimits(tileLimits);
        if (z < minZoom || z > maxZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile zoom level {z} is outside the configured range {minZoom}-{maxZoom}.");
        }

        if (!TerrainTmsGrid.ValidateTileCoordinates(z, x, y))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile coordinates z={z}, x={x}, y={y} are outside the EPSG:4326 TMS tile matrix bounds.");
        }

        return null;
    }

    private static QuantizedMeshLayerMetadataResponse BuildLayerJsonResponse(
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
        var tileUrl = $"{baseUrl}/terrain/{escapedDatasetId}/{{z}}/{{x}}/{{y}}.terrain";
        var available = metadata.IsSupported
            ? MapAvailableRanges(TerrainTmsGrid.ComputeAvailableRanges(metadata.Wgs84Bounds, minZoom, maxZoom))
            : [];

        return new QuantizedMeshLayerMetadataResponse
        {
            Name = resource.Metadata.Title ?? resource.Metadata.Name,
            Description = resource.Metadata.Description,
            Attribution = resource.Metadata.Attribution,
            Bounds = metadata.Wgs84Bounds,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Tiles = [tileUrl],
            Extensions = [QuantizedMeshTileWriter.OctVertexNormalsExtensionName],
            Available = available,
            Coverage = TerrainEndpoints.BuildCoverage(metadata, minZoom, maxZoom),
            Capabilities = TerrainEndpoints.BuildCapabilities(baseUrl, escapedDatasetId, metadata),
            Links = TerrainEndpoints.BuildLinks(baseUrl, escapedDatasetId, selfIsQuantizedMesh: true),
            Source = TerrainEndpoints.BuildSourceMetadata(datasetId, layerId, metadata),
            Supported = metadata.IsSupported,
            UnsupportedReasons = metadata.UnsupportedReasons
        };
    }

    private static QuantizedMeshTileRangeResponse[][] MapAvailableRanges(TerrainTileRange[][] ranges)
    {
        var mapped = new QuantizedMeshTileRangeResponse[ranges.Length][];
        for (var z = 0; z < ranges.Length; z++)
        {
            var level = ranges[z];
            var levelMapped = new QuantizedMeshTileRangeResponse[level.Length];
            for (var i = 0; i < level.Length; i++)
            {
                levelMapped[i] = new QuantizedMeshTileRangeResponse
                {
                    StartX = level[i].StartX,
                    StartY = level[i].StartY,
                    EndX = level[i].EndX,
                    EndY = level[i].EndY
                };
            }

            mapped[z] = levelMapped;
        }

        return mapped;
    }
}
