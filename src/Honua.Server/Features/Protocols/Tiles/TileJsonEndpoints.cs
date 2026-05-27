// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            requiredProtocol: ServiceProtocols.FeatureServer,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var resource = layerValidation.Resource!;
        var publication = layerValidation.Publication!;
        var resolvedLayerId = publication.LayerIndex ?? layerId;

        var tileLimits = limitsOptions.Value.Tiles;
        var minZoom = Math.Max(0, tileLimits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, tileLimits.MaxTileZoom);

        var extent = await ResolveExtentAsync(resource, resolvedLayerId, featureReader, cancellationToken);
        var bounds = extent.HasValue
            ? new[] { extent.Value.MinX, extent.Value.MinY, extent.Value.MaxX, extent.Value.MaxY }
            : null;

        var center = bounds == null
            ? null
            : new[] { (bounds[0] + bounds[2]) / 2.0, (bounds[1] + bounds[3]) / 2.0, (double)minZoom };

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var tilesUrl = $"{baseUrl}/tiles/{resolvedLayerId}/{{z}}/{{x}}/{{y}}.mvt";
        var styleUrl = $"{baseUrl}/api/styles/{resolvedLayerId}.json";

        var title = resource.Metadata.Title ?? resource.Metadata.Name;
        var description = resource.Metadata.Description;

        var response = new TileJsonResponse
        {
            TileJson = TileJsonVersion,
            Name = title,
            Description = description,
            Scheme = TileScheme,
            Tiles = [tilesUrl],
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = bounds,
            Center = center,
            VectorLayers = [BuildVectorLayer(resource, title, description, minZoom, maxZoom)],
            Style = styleUrl
        };

        return Results.Json(response, TileJsonContext.Default.TileJsonResponse, contentType: JsonContentType);
    }

    private static async Task<FeatureExtent?> ResolveExtentAsync(
        MetadataV2Resource resource,
        int layerId,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        var srid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = srid,
            OutputSrid = SpatialReference.WGS84.Wkid
        };

        var extent = await featureReader.GetExtentAsync(layerId, query, cancellationToken);
        if (extent.HasValue)
        {
            return extent;
        }

        var bbox = resource.ReadBbox();
        if (bbox is not null && srid == SpatialReference.WGS84.Wkid)
        {
            return FeatureExtent.Create(bbox.West, bbox.South, bbox.East, bbox.North, SpatialReference.WGS84.Wkid);
        }

        return null;
    }

    private static TileJsonVectorLayer BuildVectorLayer(
        MetadataV2Resource resource,
        string title,
        string? description,
        int minZoom,
        int maxZoom)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var geometryField = resource.FindPrimaryGeometryField();
        foreach (var field in resource.SchemaFields)
        {
            if (geometryField is not null &&
                string.Equals(field.Name, geometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            fields[field.Name] = string.IsNullOrWhiteSpace(field.Description)
                ? MapFieldTypeName(field.Type)
                : field.Description!;
        }

        var vectorDescription = string.IsNullOrWhiteSpace(description) ? title : description!;

        return new TileJsonVectorLayer
        {
            Id = StyleDefaults.SourceLayerName,
            Description = vectorDescription,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Fields = fields
        };
    }

    private static string MapFieldTypeName(MetadataV2FieldType typeName)
    {
        return typeName switch
        {
            MetadataV2FieldType.String => "string",
            MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger => "integer",
            MetadataV2FieldType.Double or MetadataV2FieldType.Float => "number",
            MetadataV2FieldType.Boolean => "boolean",
            MetadataV2FieldType.DateTime or MetadataV2FieldType.Date or MetadataV2FieldType.Time => "string",
            MetadataV2FieldType.Json => "object",
            MetadataV2FieldType.Binary => "binary",
            MetadataV2FieldType.Uuid => "string",
            _ => "string",
        };
    }
}
