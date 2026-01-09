// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// OData v4 endpoints providing intermediate conformance level.
/// Supports $filter, $select, $orderby, $top, $skip, $count, and CRUD operations.
/// </summary>
internal static partial class ODataEndpoints
{
    /// <summary>
    /// Maps OData v4 endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapODataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // OData service document
        var serviceDocument = endpoints.MapGet("/odata",
            (HttpContext context, ODataMetadataHandler handler) => handler.HandleGetServiceDocument(context))
            .WithDisplayName("OData Service Document")
            .WithName("ODataServiceDocument")
            .WithSummary("Get OData service document")
            .WithTags("OData")
            .Produces<Models.ServiceDocument>(200, "application/json")
            .Produces(404);

        // OData metadata document
        var metadata = endpoints.MapGet("/odata/$metadata",
            (HttpContext context, ODataMetadataHandler handler, CancellationToken cancellationToken) =>
                handler.HandleGetMetadataAsync(context, cancellationToken))
            .WithDisplayName("OData Metadata Document")
            .WithName("ODataMetadata")
            .WithSummary("Get OData metadata document")
            .WithTags("OData")
            .Produces<string>(200, "application/xml")
            .Produces(404);

        // OData entity sets (layers as collections)
        var layers = endpoints.MapGet("/odata/Layers",
            (HttpContext context,
                ODataQueryHandler handler,
                [FromQuery(Name = "$filter")] string? filter,
                [FromQuery(Name = "$select")] string? select,
                [FromQuery(Name = "$top")] string? top,
                [FromQuery(Name = "$skip")] string? skip,
                [FromQuery(Name = "$count")] string? count,
                CancellationToken cancellationToken) =>
                handler.HandleGetLayersAsync(context, filter, select, top, skip, count, cancellationToken))
            .WithDisplayName("OData Layers Collection")
            .WithName("ODataLayers")
            .WithSummary("Get layers collection with OData query parameters")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // OData features for a specific layer
        var features = endpoints.MapGet("/odata/Features({layerId:int})",
            (HttpContext context,
                int layerId,
                ODataStreamingQueryHandler handler,
                [FromQuery(Name = "$filter")] string? filter,
                [FromQuery(Name = "$select")] string? select,
                [FromQuery(Name = "$orderby")] string? orderby,
                [FromQuery(Name = "$top")] string? top,
                [FromQuery(Name = "$skip")] string? skip,
                [FromQuery(Name = "$count")] string? count,
                [FromQuery(Name = "$expand")] string? expand,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesAsync(context, layerId, filter, select, orderby, top, skip, count, expand, cancellationToken))
            .WithDisplayName("OData Features Collection")
            .WithName("ODataFeatures")
            .WithSummary("Get features with OData query parameters ($filter, $select, $orderby, $top, $skip, $count)")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // POST - Create a new feature
        var createFeature = endpoints.MapPost("/odata/Features({layerId:int})",
            (HttpContext context, int layerId, ODataCrudHandler handler, Models.ODataFeatureRequest request, CancellationToken cancellationToken) =>
                handler.HandleCreateFeatureAsync(context, layerId, request, cancellationToken))
            .WithDisplayName("OData Create Feature")
            .WithName("ODataCreateFeature")
            .WithSummary("Create a new feature in the specified layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(201, "application/json")
            .Produces(400)
            .Produces(404);
        createFeature.RequireAuthorization();

        // GET - Get a single feature
        var getFeature = endpoints.MapGet("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleGetSingleFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Get Feature")
            .WithName("ODataGetFeature")
            .WithSummary("Get a single feature by ID")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        // PATCH - Update an existing feature
        var updateFeature = endpoints.MapPatch("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, ODataCrudHandler handler, Models.ODataFeatureRequest request, CancellationToken cancellationToken) =>
                handler.HandleUpdateFeatureAsync(context, layerId, objectId, request, cancellationToken))
            .WithDisplayName("OData Update Feature")
            .WithName("ODataUpdateFeature")
            .WithSummary("Update an existing feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        updateFeature.RequireAuthorization();

        // DELETE - Delete a feature
        var deleteFeature = endpoints.MapDelete("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Delete Feature")
            .WithName("ODataDeleteFeature")
            .WithSummary("Delete a feature")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);
        deleteFeature.RequireAuthorization();

        // POST - Batch operations
        var batch = endpoints.MapPost("/odata/$batch",
            (HttpContext context, ODataBatchOperationHandler handler, CancellationToken cancellationToken) =>
                handler.HandleBatchRequestAsync(context, cancellationToken))
            .WithDisplayName("OData Batch Operations")
            .WithName("ODataBatch")
            .WithSummary("Execute multiple operations in a single request with optional atomicity groups")
            .WithTags("OData")
            .Produces<Models.ODataBatchResponse>(200, "application/json")
            .Produces(400);
        batch.RequireAuthorization();

        // GET - Aggregation with $apply
        var apply = endpoints.MapGet("/odata/Features({layerId:int})/$apply",
            (HttpContext context,
                int layerId,
                ODataAdvancedQueryHandler handler,
                [FromQuery(Name = "$apply")] string? apply,
                [FromQuery(Name = "$filter")] string? filter,
                CancellationToken cancellationToken) =>
                handler.HandleApplyAsync(context, layerId, apply, filter, cancellationToken))
            .WithDisplayName("OData Aggregation")
            .WithName("ODataApply")
            .WithSummary("Aggregate features using $apply transformations (aggregate, groupby, filter, compute)")
            .WithTags("OData")
            .Produces<Models.ODataAggregationResult>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // GET - Full-text search with $search
        var search = endpoints.MapGet("/odata/Features({layerId:int})/$search",
            (HttpContext context,
                int layerId,
                ODataAdvancedQueryHandler handler,
                [FromQuery(Name = "$search")] string? search,
                [FromQuery(Name = "$top")] string? top,
                [FromQuery(Name = "$skip")] string? skip,
                [FromQuery(Name = "$count")] string? count,
                CancellationToken cancellationToken) =>
                handler.HandleSearchAsync(context, layerId, search, top, skip, count, cancellationToken))
            .WithDisplayName("OData Search")
            .WithName("ODataSearch")
            .WithSummary("Full-text search across feature attributes using PostgreSQL text search")
            .WithTags("OData")
            .Produces<Models.ODataSearchResult>(200, "application/json")
            .Produces(400)
            .Produces(404);

        return endpoints;
    }
}
