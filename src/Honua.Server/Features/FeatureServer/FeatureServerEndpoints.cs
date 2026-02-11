// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using Honua.Server.Features.Infrastructure.Caching;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Extension methods to register FeatureServer endpoints
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Maps FeatureServer REST API endpoints for layer metadata using AOT-compatible routing
    /// </summary>
    public static IEndpointRouteBuilder MapFeatureServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var serviceMetadata = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer", (Delegate)HandleGetServiceMetadata)
            .WithDisplayName("Get FeatureServer Service Metadata")
            .WithName("GetServiceMetadata")
            .WithSummary("Get FeatureServer service metadata")
            .WithDescription("Returns metadata for a FeatureServer service including all layers")
            .WithTags("FeatureServer")
            .CacheOutput("ServiceMetadata")
            .WithETag();
        // .Produces<FeatureServerResponse>(200, "application/json")
        // .Produces(404);

        var layerMetadata = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}", (Delegate)HandleLayerMetadata)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .CacheOutput("LayerMetadata")
            .WithETag();
        // .Produces<LayerResponse>(200, "application/json")
        // .Produces(404);

        var queryGet = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesGet)
            .WithDisplayName("Query FeatureServer Features (GET)")
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features from a FeatureServer layer using GET")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        var queryPost = endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesPost)
            .WithDisplayName("Query FeatureServer Features (POST)")
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features from a FeatureServer layer using POST")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        var generateRenderer = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/generateRenderer", (Delegate)HandleGenerateRenderer)
            .WithDisplayName("Generate Renderer")
            .WithName("GenerateRenderer")
            .WithSummary("Generate a renderer for a FeatureServer layer")
            .WithDescription("Generates a renderer definition based on classification parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces(400)
        // .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/applyEdits", HandleServiceApplyEdits)
            .WithDisplayName("Apply Service-Level Feature Edits")
            .WithName("ServiceApplyEdits")
            .WithSummary("Apply feature edits across multiple layers")
            .WithDescription("Apply feature edits to multiple layers in a single request including add, update, and delete operations")
            .WithTags("FeatureServer")
            .RequireAuthorization();

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/applyEdits", HandleApplyEdits)
            .WithDisplayName("Apply Feature Edits")
            .WithName("ApplyEdits")
            .WithSummary("Apply feature edits (add, update, delete)")
            .WithDescription("Apply feature edits to a layer including add, update, and delete operations")
            .WithTags("FeatureServer")
            .RequireAuthorization();
        // .Produces<ApplyEditsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/addFeatures", HandleAddFeatures)
            .WithDisplayName("Add Features")
            .WithName("AddFeatures")
            .WithSummary("Add new features to a layer")
            .WithDescription("Adds one or more features to a layer")
            .WithTags("FeatureServer")
            .RequireAuthorization();

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/updateFeatures", HandleUpdateFeatures)
            .WithDisplayName("Update Features")
            .WithName("UpdateFeatures")
            .WithSummary("Update existing features in a layer")
            .WithDescription("Updates one or more features in a layer")
            .WithTags("FeatureServer")
            .RequireAuthorization();

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/deleteFeatures", HandleDeleteFeatures)
            .WithDisplayName("Delete Features")
            .WithName("DeleteFeatures")
            .WithSummary("Delete features from a layer")
            .WithDescription("Deletes one or more features from a layer")
            .WithTags("FeatureServer")
            .RequireAuthorization();

        var relatedGet = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsGet)
            .WithDisplayName("Query Related Records (GET)")
            .WithName("QueryRelatedRecordsGet")
            .WithSummary("Query features related to source features through a relationship using GET")
            .WithDescription("Returns features from a related layer based on relationship definitions via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        var relatedPost = endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsPost)
            .WithDisplayName("Query Related Records (POST)")
            .WithName("QueryRelatedRecordsPost")
            .WithSummary("Query features related to source features through a relationship using POST")
            .WithDescription("Returns features from a related layer based on relationship definitions via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        var layerTile = endpoints.MapGet("/tiles/{layerId:int}/{z:int}/{x:int}/{y:int}.mvt", HandleLayerTile)
            .WithDisplayName("Get MVT Tile")
            .WithName("GetMvtTile")
            .WithSummary("Get MVT (Mapbox Vector Tile) for a layer")
            .WithDescription("Generates vector tiles using PostGIS ST_AsMVT with proper clipping and simplification")
            .WithTags("Tiles")
            .CacheOutput("MvtTile");
        // .Produces<byte[]>(200, "application/vnd.mapbox-vector-tile")
        // .Produces(204)
        // .Produces(400)
        // .Produces(404);

        return endpoints;
    }
}
