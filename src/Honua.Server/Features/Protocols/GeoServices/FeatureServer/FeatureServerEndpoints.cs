// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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
            .WithETag()
            .Produces<FeatureServerResponse>(200, "application/json")
            .Produces(404);

        var layerMetadata = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}", (Delegate)HandleLayerMetadata)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .CacheOutput("LayerMetadata")
            .WithETag()
            .Produces<LayerResponse>(200, "application/json")
            .Produces(404)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        var queryGet = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesGet)
            .WithDisplayName("Query FeatureServer Features (GET)")
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features from a FeatureServer layer using GET")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<QueryResponse>(200, "application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/geo+json")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-protobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.flatgeobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/geobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.apache.parquet")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.apache.arrow.stream")
            .Produces(400)
            .Produces(404);

        var queryPost = endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesPost)
            .WithDisplayName("Query FeatureServer Features (POST)")
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features from a FeatureServer layer using POST")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<QueryResponse>(200, "application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/geo+json")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-protobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.flatgeobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/geobuf")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.apache.parquet")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.apache.arrow.stream")
            .Produces(400)
            .Produces(404);

        var serviceQueryGet = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/query", HandleServiceQueryFeaturesGet)
            .WithDisplayName("Query FeatureServer Service (GET)")
            .WithName("QueryFeatureServiceGet")
            .WithSummary("Query features from a FeatureServer service using GET")
            .WithDescription("GET-only service-level query endpoint that returns per-layer results for the selected accessible layers")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ServiceQueryResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        var generateRenderer = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/generateRenderer", (Delegate)HandleGenerateRenderer)
            .WithDisplayName("Generate Renderer")
            .WithName("GenerateRenderer")
            .WithSummary("Generate a renderer for a FeatureServer layer")
            .WithDescription("Generates a renderer definition based on classification parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
        .Produces(400)
        .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/applyEdits", HandleServiceApplyEdits)
            .WithDisplayName("Apply Service-Level Feature Edits")
            .WithName("ServiceApplyEdits")
            .WithSummary("Apply feature edits across multiple layers")
            .WithDescription("Apply feature edits to multiple layers in a single request including add, update, and delete operations")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<ServiceApplyEditsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/applyEdits", HandleApplyEdits)
            .WithDisplayName("Apply Feature Edits")
            .WithName("ApplyEdits")
            .WithSummary("Apply feature edits (add, update, delete)")
            .WithDescription("Apply feature edits to a layer including add, update, and delete operations")
            .WithTags("FeatureServer")
            .AllowAnonymous()
        .Produces<ApplyEditsResponse>(200, "application/json")
        .Produces(400)
        .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/addFeatures", HandleAddFeatures)
            .WithDisplayName("Add Features")
            .WithName("AddFeatures")
            .WithSummary("Add new features to a layer")
            .WithDescription("Adds one or more features to a layer")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<ApplyEditsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/updateFeatures", HandleUpdateFeatures)
            .WithDisplayName("Update Features")
            .WithName("UpdateFeatures")
            .WithSummary("Update existing features in a layer")
            .WithDescription("Updates one or more features in a layer")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<ApplyEditsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/deleteFeatures", HandleDeleteFeatures)
            .WithDisplayName("Delete Features")
            .WithName("DeleteFeatures")
            .WithSummary("Delete features from a layer")
            .WithDescription("Deletes one or more features from a layer")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<ApplyEditsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        var relatedGet = endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsGet)
            .WithDisplayName("Query Related Records (GET)")
            .WithName("QueryRelatedRecordsGet")
            .WithSummary("Query features related to source features through a relationship using GET")
            .WithDescription("Returns features from a related layer based on relationship definitions via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
        .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        .Produces(400)
        .Produces(404);

        var relatedPost = endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsPost)
            .WithDisplayName("Query Related Records (POST)")
            .WithName("QueryRelatedRecordsPost")
            .WithSummary("Query features related to source features through a relationship using POST")
            .WithDescription("Returns features from a related layer based on relationship definitions via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
        .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        .Produces(400)
        .Produces(404);

        var layerTile = endpoints.MapGet("/tiles/{layerId:int}/{z:int}/{x:int}/{y:int}.mvt", HandleLayerTile)
            .WithDisplayName("Get MVT Tile")
            .WithName("GetMvtTile")
            .WithSummary("Get MVT (Mapbox Vector Tile) for a layer")
            .WithDescription("Generates vector tiles using PostGIS ST_AsMVT with proper clipping and simplification")
            .WithTags("Tiles")
            .CacheOutput("MvtTile")
        .Produces<byte[]>(200, "application/vnd.mapbox-vector-tile")
        .Produces(204)
        .Produces(400)
        .Produces(404);

        // Replication endpoints
        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/replicas", HandleReplicas)
            .WithDisplayName("List Replicas")
            .WithName("Replicas")
            .WithSummary("List replicas for a feature service")
            .WithDescription("Returns registered replica metadata for the feature service")
            .WithTags("FeatureServer")
            .Produces<ReplicaSummary[]>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/replicas/{replicaId}", HandleReplicaInfo)
            .WithDisplayName("Get Replica Info")
            .WithName("ReplicaInfo")
            .WithSummary("Get metadata for a specific replica")
            .WithDescription("Returns replica metadata for a specific registered replica")
            .WithTags("FeatureServer")
            .Produces<ReplicaInfoResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/createReplica", HandleCreateReplica)
            .WithDisplayName("Create Replica")
            .WithName("CreateReplica")
            .WithSummary("Create a replica for offline use or synchronization")
            .WithDescription("Creates a replica of specified layers for offline editing and synchronization")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<CreateReplicaResponse>(200, "application/json")
            .Produces(400)
            .Produces(404)
            .Produces(503);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/extractChanges", HandleExtractChanges)
            .WithDisplayName("Extract Changes")
            .WithName("ExtractChanges")
            .WithSummary("Extract changes since last synchronization")
            .WithDescription("Returns changes made since the last synchronization for a registered replica")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<ExtractChangesResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/synchronizeReplica", HandleSynchronizeReplica)
            .WithDisplayName("Synchronize Replica")
            .WithName("SynchronizeReplica")
            .WithSummary("Synchronize a replica with the server")
            .WithDescription("Applies edits from a replica to the server and returns server changes")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<SynchronizeReplicaResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/unRegisterReplica", HandleUnRegisterReplica)
            .WithDisplayName("Unregister Replica")
            .WithName("UnRegisterReplica")
            .WithSummary("Unregister a replica")
            .WithDescription("Removes a registered replica and frees associated resources")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<SuccessResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // Maintenance/utility endpoints
        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/append", HandleServiceAppend)
            .WithDisplayName("Append Features (Service)")
            .WithName("ServiceAppend")
            .WithSummary("Append features to a service layer")
            .WithDescription("Bulk append features to a layer within the service")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<AppendResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/append", HandleLayerAppend)
            .WithDisplayName("Append Features (Layer)")
            .WithName("LayerAppend")
            .WithSummary("Append features to a specific layer")
            .WithDescription("Bulk append features to a specific layer")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<AppendResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/calculate", HandleCalculate)
            .WithDisplayName("Calculate")
            .WithName("Calculate")
            .WithSummary("Calculate field values for features")
            .WithDescription("Calculates new field values using expressions for matching features")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<CalculateResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/calculate", HandleCalculate)
            .WithDisplayName("Calculate (POST)")
            .WithName("CalculatePost")
            .WithSummary("Calculate field values for features")
            .WithDescription("Calculates new field values using expressions for matching features")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<CalculateResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/getEstimates", HandleGetEstimates)
            .WithDisplayName("Get Estimates (Layer)")
            .WithName("GetEstimatesLayer")
            .WithSummary("Get approximate count and extent estimates for a layer")
            .WithDescription("Returns estimated feature count and spatial extent using catalog statistics")
            .WithTags("FeatureServer");

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/getEstimates", HandleServiceGetEstimates)
            .WithDisplayName("Get Estimates (Service)")
            .WithName("GetEstimatesService")
            .WithSummary("Get approximate count and extent estimates for a service")
            .WithDescription("Returns per-layer estimated feature count and spatial extent for the selected accessible layers")
            .WithTags("FeatureServer")
            .Produces<ServiceGetEstimatesResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryTopFeatures", HandleQueryTopFeaturesGet)
            .WithDisplayName("Query Top Features (GET)")
            .WithName("QueryTopFeaturesGet")
            .WithSummary("Query top features per group using GET")
            .WithDescription("Returns top N features per group based on a topFilter specification")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryTopFeatures", HandleQueryTopFeaturesPost)
            .WithDisplayName("Query Top Features (POST)")
            .WithName("QueryTopFeaturesPost")
            .WithSummary("Query top features per group using POST")
            .WithDescription("Returns top N features per group based on a topFilter specification")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryDateBins", HandleQueryDateBinsGet)
            .WithDisplayName("Query Date Bins (GET)")
            .WithName("QueryDateBinsGet")
            .WithSummary("Query features binned by date intervals using GET")
            .WithDescription("Groups features into temporal bins and returns aggregate statistics per bin")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryDateBins", HandleQueryDateBinsPost)
            .WithDisplayName("Query Date Bins (POST)")
            .WithName("QueryDateBinsPost")
            .WithSummary("Query features binned by date intervals using POST")
            .WithDescription("Groups features into temporal bins and returns aggregate statistics per bin")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryBins", HandleQueryBinsGet)
            .WithDisplayName("Query Bins (GET)")
            .WithName("QueryBinsGet")
            .WithSummary("Query features binned by numeric or classification intervals using GET")
            .WithDescription("Groups features into bins using various algorithms and returns aggregate statistics per bin")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryBins", HandleQueryBinsPost)
            .WithDisplayName("Query Bins (POST)")
            .WithName("QueryBinsPost")
            .WithSummary("Query features binned by numeric or classification intervals using POST")
            .WithDescription("Groups features into bins using various algorithms and returns aggregate statistics per bin")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/queryDomains", HandleQueryDomains)
            .WithDisplayName("Query Domains")
            .WithName("QueryDomains")
            .WithSummary("Query coded-value domains for the service")
            .WithDescription("Returns schema-defined domains for accessible service layers")
            .WithTags("FeatureServer")
            .Produces<QueryDomainsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/relationships", HandleQueryRelationships)
            .WithDisplayName("Query Relationships")
            .WithName("QueryRelationships")
            .WithSummary("Query relationship metadata for a service")
            .WithDescription("Returns relationship definitions for accessible layers without exposing hidden related layers")
            .WithTags("FeatureServer")
            .Produces<QueryRelationshipsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/validateSQL", HandleValidateSql)
            .WithDisplayName("Validate SQL")
            .WithName("ValidateSQL")
            .WithSummary("Validate a SQL WHERE clause")
            .WithDescription("Validates a SQL expression against a layer schema and returns whether it is syntactically valid")
            .WithTags("FeatureServer");

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryH3", HandleQueryH3Get)
            .WithDisplayName("Query H3 (GET)")
            .WithName("QueryH3Get")
            .WithSummary("Query features aggregated by H3 hexagonal grid cells using GET")
            .WithDescription("Groups features into H3 cells at a configurable resolution and returns aggregate statistics per cell")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryH3", HandleQueryH3Post)
            .WithDisplayName("Query H3 (POST)")
            .WithName("QueryH3Post")
            .WithSummary("Query features aggregated by H3 hexagonal grid cells using POST")
            .WithDescription("Groups features into H3 cells at a configurable resolution and returns aggregate statistics per cell")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        endpoints.MapGet("/tiles/{layerId:int}/h3/{z:int}/{x:int}/{y:int}.mvt", HandleH3Tile)
            .WithDisplayName("Get H3 MVT Tile")
            .WithName("GetH3MvtTile")
            .WithSummary("Get MVT tile containing H3 hexagonal cell boundaries")
            .WithDescription("Generates vector tiles with H3 cell boundaries and feature counts, with resolution from zoom or query parameter")
            .WithTags("Tiles")
            .CacheOutput("H3MvtTile");

        return endpoints;
    }
}
