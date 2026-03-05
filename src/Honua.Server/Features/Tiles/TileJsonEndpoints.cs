// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Honua.Server.Features.Tiles;

internal static class TileJsonEndpoints
{
    private const string TileJsonVersion = "3.0.0";
    private const string TileScheme = "xyz";

    public static IEndpointRouteBuilder MapTileJsonEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tiles/{layerId:int}/tile.json", HandleGetTileJson)
            .WithDisplayName("Get TileJSON Metadata")
            .WithName("GetTileJson")
            .WithSummary("Get TileJSON metadata for a layer")
            .WithDescription("Returns TileJSON 3.0 metadata for MapLibre client configuration")
            .WithTags("Tiles")
            .CacheOutput("TileJson")
            .WithETag()
            .Produces<TileJsonResponse>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapPost("/api/tiles/{layerId:int}/export", HandleStartPmTilesExport)
            .WithDisplayName("Start PMTiles Export")
            .WithName("StartPmTilesExport")
            .WithSummary("Queue PMTiles export for a layer")
            .WithDescription("Queues an asynchronous PMTiles export job and returns a tile-operation status URL")
            .WithTags("Tiles", "Admin")
            .RequireAdminAuthorization()
            .Produces<TileExportStartResponse>(StatusCodes.Status202Accepted, MediaTypes.Json)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> HandleGetTileJson(
        int layerId,
        HttpContext context,
        [FromServices] IFeatureReader featureReader,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        var tileLimits = limitsOptions.Value.Tiles;
        var minZoom = Math.Max(0, tileLimits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, tileLimits.MaxTileZoom);

        var extent = await ResolveExtentAsync(layer, featureReader, cancellationToken);
        var bounds = extent.HasValue
            ? new[] { extent.Value.MinX, extent.Value.MinY, extent.Value.MaxX, extent.Value.MaxY }
            : null;

        var center = bounds == null
            ? null
            : new[] { (bounds[0] + bounds[2]) / 2.0, (bounds[1] + bounds[3]) / 2.0, (double)minZoom };

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var tilesUrl = $"{baseUrl}/tiles/{layer.Id}/{{z}}/{{x}}/{{y}}.mvt";
        var styleUrl = $"{baseUrl}/api/styles/{layer.Id}.json";

        var response = new TileJsonResponse
        {
            TileJson = TileJsonVersion,
            Name = layer.Name,
            Description = layer.Description,
            Scheme = TileScheme,
            Tiles = [tilesUrl],
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = bounds,
            Center = center,
            VectorLayers = [BuildVectorLayer(layer, minZoom, maxZoom)],
            Style = styleUrl
        };

        return Results.Json(response, TileJsonContext.Default.TileJsonResponse, contentType: MediaTypes.Json);
    }

    private static async Task<FeatureExtent?> ResolveExtentAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = SpatialReference.WGS84.Wkid
        };

        var extent = await featureReader.GetExtentAsync(layer.Id, query, cancellationToken);
        if (extent.HasValue)
        {
            return extent;
        }

        if (layer.Extent.HasValue && layer.Extent.Value.SpatialReference == SpatialReference.WGS84.Wkid)
        {
            return layer.Extent.Value;
        }

        return null;
    }

    private static TileJsonVectorLayer BuildVectorLayer(LayerDefinition layer, int minZoom, int maxZoom)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layer.Fields)
        {
            if (field.IsGeometry)
            {
                continue;
            }

            fields[field.Name] = DescribeField(field);
        }

        var description = string.IsNullOrWhiteSpace(layer.Description) ? layer.Name : layer.Description;

        return new TileJsonVectorLayer
        {
            Id = StyleDefaults.SourceLayerName,
            Description = description,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Fields = fields
        };
    }

    private static string DescribeField(FieldDefinition field)
        => string.IsNullOrWhiteSpace(field.Description) ? MapFieldType(field.Type) : field.Description!;

    private static string MapFieldType(FieldType type)
        => type switch
        {
            FieldType.String => "string",
            FieldType.Integer => "integer",
            FieldType.BigInteger => "integer",
            FieldType.Double => "number",
            FieldType.Float => "number",
            FieldType.Boolean => "boolean",
            FieldType.DateTime => "string",
            FieldType.Date => "string",
            FieldType.Time => "string",
            FieldType.Json => "object",
            FieldType.Binary => "binary",
            FieldType.Uuid => "string",
            _ => "string"
        };

    private static async Task<IResult> HandleStartPmTilesExport(
        int layerId,
        HttpContext context,
        [FromQuery] string? format,
        [FromQuery] int? minZoom,
        [FromQuery] int? maxZoom,
        [FromQuery] int? maxTiles,
        [FromQuery] string? tileMatrixSetId,
        [FromQuery] string? bbox,
        [FromServices] ITileOperationJobService tileOperationJobService,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(format, "pmtiles", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid export parameters",
                ["Query parameter 'format' must be 'pmtiles'."]);
        }

        if (minZoom.HasValue && maxZoom.HasValue && minZoom > maxZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid export parameters",
                ["Query parameter 'minZoom' must be less than or equal to 'maxZoom'."]);
        }

        if (maxTiles is <= 0)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid export parameters",
                ["Query parameter 'maxTiles' must be greater than zero when provided."]);
        }

        if (!TryParseBbox(bbox, out var parsedBbox, out var bboxError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid export parameters",
                [bboxError ?? "Query parameter 'bbox' must contain four comma-separated numeric values."]);
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var request = new TileOperationStartRequest
        {
            Operation = "export_pmtiles",
            ServiceId = layerValidation.Service?.Name,
            LayerId = layerId,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            TileMatrixSetId = tileMatrixSetId,
            Bbox = parsedBbox,
            MaxTiles = maxTiles
        };

        string jobId;
        try
        {
            jobId = await tileOperationJobService.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }

        var response = new TileExportStartResponse
        {
            JobId = jobId,
            Format = "pmtiles",
            Message = "PMTiles export job queued.",
            StatusUrl = $"/api/v1/admin/tile-operations/jobs/{jobId}"
        };

        return Results.Json(
            response,
            TileJsonContext.Default.TileExportStartResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static bool TryParseBbox(string? bbox, out double[]? values, out string? error)
    {
        values = null;
        error = null;

        if (string.IsNullOrWhiteSpace(bbox))
        {
            return true;
        }

        var segments = bbox.Split(',', StringSplitOptions.TrimEntries);
        if (segments.Length != 4)
        {
            error = "Query parameter 'bbox' must contain exactly four values: minLon,minLat,maxLon,maxLat.";
            return false;
        }

        values = new double[4];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!double.TryParse(segments[i], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
            {
                error = "Query parameter 'bbox' must contain valid numeric values.";
                values = null;
                return false;
            }

            values[i] = parsed;
        }

        return true;
    }
}
