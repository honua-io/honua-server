// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer;

/// <summary>
/// Maps MapServer REST API endpoints for dynamic map image generation.
/// </summary>
internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Maps MapServer REST API endpoints using AOT-compatible routing.
    /// </summary>
    public static IEndpointRouteBuilder MapMapServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/MapServer",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetServiceMetadata(context))
            .WithDisplayName("Get MapServer Service Metadata")
            .WithName("GetMapServerMetadata")
            .WithSummary("Get MapServer service metadata")
            .WithDescription("Returns metadata for a MapServer service including all layers")
            .WithTags("MapServer")
            .CacheOutput("ServiceMetadata");

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/{layerId:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetLayerMetadata(context))
            .WithDisplayName("Get MapServer Layer Metadata")
            .WithName("GetMapServerLayerMetadata")
            .WithSummary("Get MapServer layer metadata")
            .WithDescription("Returns metadata for a specific MapServer layer")
            .WithTags("MapServer")
            .CacheOutput("LayerMetadata");

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/export",
                static (HttpContext context, CancellationToken cancellationToken) => HandleExport(context))
            .WithDisplayName("Export Map Image")
            .WithName("MapServerExport")
            .WithSummary("Export a map image")
            .WithDescription("Generates a raster map image from layer features with MapLibre styling")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/export",
                static (HttpContext context, CancellationToken cancellationToken) => HandleExport(context))
            .WithDisplayName("Export Map Image (POST)")
            .WithName("MapServerExportPost")
            .WithSummary("Export a map image using POST")
            .WithDescription("Generates a raster map image from layer features with MapLibre styling")
            .WithTags("MapServer");

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/identify",
                static (HttpContext context, CancellationToken cancellationToken) => HandleIdentify(context))
            .WithDisplayName("Identify Features")
            .WithName("MapServerIdentify")
            .WithSummary("Identify features at a location")
            .WithDescription("Identifies features at a given point on the map")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/identify",
                static (HttpContext context, CancellationToken cancellationToken) => HandleIdentify(context))
            .WithDisplayName("Identify Features (POST)")
            .WithName("MapServerIdentifyPost")
            .WithSummary("Identify features at a location using POST")
            .WithDescription("Identifies features at a given point on the map")
            .WithTags("MapServer");

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/legend",
                static (HttpContext context, CancellationToken cancellationToken) => HandleLegend(context))
            .WithDisplayName("Get Map Legend")
            .WithName("MapServerLegend")
            .WithSummary("Get map legend")
            .WithDescription("Returns legend information with swatch images for all visible layers")
            .WithTags("MapServer")
            .CacheOutput("ServiceMetadata");

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/{layerId:int}/query", HandleLayerQueryGet)
            .WithDisplayName("Query MapServer Layer (GET)")
            .WithName("MapServerQueryGet")
            .WithSummary("Query features from a MapServer layer using GET")
            .WithDescription("Query features using the FeatureServer query handler")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/{layerId:int}/query", HandleLayerQueryPost)
            .WithDisplayName("Query MapServer Layer (POST)")
            .WithName("MapServerQueryPost")
            .WithSummary("Query features from a MapServer layer using POST")
            .WithDescription("Query features using the FeatureServer query handler")
            .WithTags("MapServer");

        return endpoints;
    }
}
