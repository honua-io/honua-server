// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Read-only Esri-compatible MapServer surface; anonymous by design. The POST
// variants of export/generateKml/identify/find/query operations mirror the GET
// form (the Esri REST API accepts the same parameters in either an URL query
// string or a request body) and perform no server-side mutation. Each POST
// route opts into AllowAnonymous explicitly so authorization-policy tooling
// can see the intent rather than treating it as an accidental gap.

namespace Honua.Protocols.GeoServices.MapServer;

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

        // Esri clients hydrate metadata by POSTing {"f":"json"}; mirror the GET
        // form so discovery succeeds. Anonymous by design and without the
        // CacheOutput companion, matching the export/identify POST variants.
        endpoints.MapPost("/rest/services/{serviceId}/MapServer",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetServiceMetadata(context))
            .WithDisplayName("Get MapServer Service Metadata (POST)")
            .WithName("GetMapServerMetadataPost")
            .WithSummary("Get MapServer service metadata using POST")
            .WithDescription("Returns metadata for a MapServer service including all layers")
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/{layerId:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetLayerMetadata(context))
            .WithDisplayName("Get MapServer Layer Metadata")
            .WithName("GetMapServerLayerMetadata")
            .WithSummary("Get MapServer layer metadata")
            .WithDescription("Returns metadata for a specific MapServer layer")
            .WithTags("MapServer")
            .CacheOutput("LayerMetadata");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/{layerId:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetLayerMetadata(context))
            .WithDisplayName("Get MapServer Layer Metadata (POST)")
            .WithName("GetMapServerLayerMetadataPost")
            .WithSummary("Get MapServer layer metadata using POST")
            .WithDescription("Returns metadata for a specific MapServer layer")
            .WithTags("MapServer")
            .AllowAnonymous();

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
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/generateKml",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGenerateKml(context))
            .WithDisplayName("Generate KML")
            .WithName("MapServerGenerateKml")
            .WithSummary("Generate KML or KMZ from map layers")
            .WithDescription("Exports layer features as KML or KMZ")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/generateKml",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGenerateKml(context))
            .WithDisplayName("Generate KML (POST)")
            .WithName("MapServerGenerateKmlPost")
            .WithSummary("Generate KML or KMZ from map layers using POST")
            .WithDescription("Exports layer features as KML or KMZ")
            .WithTags("MapServer")
            .AllowAnonymous();

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
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/legend",
                static (HttpContext context, CancellationToken cancellationToken) => HandleLegend(context))
            .WithDisplayName("Get Map Legend")
            .WithName("MapServerLegend")
            .WithSummary("Get map legend")
            .WithDescription("Returns legend information with swatch images for all visible layers")
            .WithTags("MapServer")
            .CacheOutput("MapServerLegend");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/legend",
                static (HttpContext context, CancellationToken cancellationToken) => HandleLegend(context))
            .WithDisplayName("Get Map Legend (POST)")
            .WithName("MapServerLegendPost")
            .WithSummary("Get map legend using POST")
            .WithDescription("Returns legend information with swatch images for all visible layers")
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/find",
                static (HttpContext context, CancellationToken cancellationToken) => HandleFind(context))
            .WithDisplayName("Find Features")
            .WithName("MapServerFind")
            .WithSummary("Find features by text search across layers")
            .WithDescription("Searches for features matching text across multiple layers")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/find",
                static (HttpContext context, CancellationToken cancellationToken) => HandleFind(context))
            .WithDisplayName("Find Features (POST)")
            .WithName("MapServerFindPost")
            .WithSummary("Find features by text search across layers using POST")
            .WithDescription("Searches for features matching text across multiple layers")
            .WithTags("MapServer")
            .AllowAnonymous();

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
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/query", HandleServiceQueryGet)
            .WithDisplayName("Query MapServer Service (GET)")
            .WithName("MapServerServiceQueryGet")
            .WithSummary("Query features from a MapServer service using GET")
            .WithDescription("Service-level query endpoint that delegates to a target layer provided by layerId/layers")
            .WithTags("MapServer");

        endpoints.MapPost("/rest/services/{serviceId}/MapServer/query", HandleServiceQueryPost)
            .WithDisplayName("Query MapServer Service (POST)")
            .WithName("MapServerServiceQueryPost")
            .WithSummary("Query features from a MapServer service using POST")
            .WithDescription("Service-level query endpoint that delegates to a target layer provided by layerId/layers")
            .WithTags("MapServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{serviceId}/MapServer/tile/{z:int}/{y:int}/{x:int}",
                static (HttpContext context, CancellationToken cancellationToken) => HandleTile(context))
            .WithDisplayName("Get Map Tile")
            .WithName("MapServerTile")
            .WithSummary("Get a cached raster map tile")
            .WithDescription("Returns a PNG tile rendered from MapServer layer features")
            .WithTags("MapServer")
            .CacheOutput("MapServerTile");

        return endpoints;
    }
}
