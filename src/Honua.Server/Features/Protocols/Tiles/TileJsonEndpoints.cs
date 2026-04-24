// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Tiles;

internal static class TileJsonEndpoints
{
    private const string TileJsonVersion = "3.0.0";
    private const string TileScheme = "xyz";
    private const string JsonContentType = "application/json";

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
            .Produces<TileJsonResponse>(StatusCodes.Status200OK, JsonContentType)
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

        return Results.Json(response, TileJsonContext.Default.TileJsonResponse, contentType: JsonContentType);
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
}
