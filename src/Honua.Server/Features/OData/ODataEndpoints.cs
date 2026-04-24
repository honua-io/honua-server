// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;
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
            (HttpContext context, [FromServices] ODataMetadataHandler handler) => handler.HandleGetServiceDocument(context))
            .WithDisplayName("OData Service Document")
            .WithName("ODataServiceDocument")
            .WithSummary("Get OData service document")
            .WithTags("OData")
            .Produces<Models.ServiceDocument>(200, "application/json")
            .Produces(404);

        // OData metadata document
        var metadata = endpoints.MapGet("/odata/$metadata",
            (HttpContext context, [FromServices] ODataMetadataHandler handler, CancellationToken cancellationToken) =>
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
                [FromServices] ODataQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetLayersAsync(
                    context,
                    query.Filter,
                    query.Select,
                    query.Orderby,
                    query.Top,
                    query.Skip,
                    query.Skiptoken,
                    query.Count,
                    query.Format,
                    cancellationToken))
            .WithDisplayName("OData Layers Collection")
            .WithName("ODataLayers")
            .WithSummary("Get layers collection with OData query parameters")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        var layersCount = endpoints.MapGet("/odata/Layers/$count",
            (HttpContext context,
                [FromServices] ODataQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetLayersCountAsync(context, query.Filter, query.Format, cancellationToken))
            .WithDisplayName("OData Layers Count")
            .WithName("ODataLayersCount")
            .WithSummary("Get the count of layers")
            .WithTags("OData")
            .Produces<string>(200, "text/plain")
            .Produces(400)
            .Produces(404);

        // OData single layer
        var layer = endpoints.MapGet("/odata/Layers({layerId:int})",
            (HttpContext context,
                int layerId,
                [FromServices] ODataQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetLayerAsync(context, layerId, query.Select, query.Format, cancellationToken))
            .WithDisplayName("OData Layer")
            .WithName("ODataLayer")
            .WithSummary("Get a single layer by ID")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        // OData features collection (all layers requires LayerId filter)
        var features = endpoints.MapGet("/odata/Features",
            (HttpContext context,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesAsync(
                    context,
                    null,
                    query.Filter,
                    query.Select,
                    query.Orderby,
                    query.Top,
                    query.Skip,
                    query.Skiptoken,
                    query.Count,
                    query.Expand,
                    query.Compute,
                    query.Search,
                    query.Apply,
                    query.Deltatoken,
                    query.Format,
                    cancellationToken))
            .WithDisplayName("OData Features Collection")
            .WithName("ODataFeatures")
            .WithSummary("Get features with OData query parameters ($filter, $select, $orderby, $top, $skip, $count, $search, $apply)")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        var featuresCount = endpoints.MapGet("/odata/Features/$count",
            (HttpContext context,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesCountAsync(context, null, query.Filter, query.Format, cancellationToken))
            .WithDisplayName("OData Features Count")
            .WithName("ODataFeaturesCount")
            .WithSummary("Get the count of features")
            .WithTags("OData")
            .Produces<string>(200, "text/plain")
            .Produces(400)
            .Produces(404);

        // OData features for a specific layer (canonical)
        var layerFeatures = endpoints.MapGet("/odata/Layers({layerId:int})/Features",
            (HttpContext context,
                int layerId,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesAsync(
                    context,
                    layerId,
                    query.Filter,
                    query.Select,
                    query.Orderby,
                    query.Top,
                    query.Skip,
                    query.Skiptoken,
                    query.Count,
                    query.Expand,
                    query.Compute,
                    query.Search,
                    query.Apply,
                    query.Deltatoken,
                    query.Format,
                    cancellationToken))
            .WithDisplayName("OData Layer Features Collection")
            .WithName("ODataLayerFeatures")
            .WithSummary("Get features for a layer with OData query parameters")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        var layerFeaturesCount = endpoints.MapGet("/odata/Layers({layerId:int})/Features/$count",
            (HttpContext context,
                int layerId,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesCountAsync(context, layerId, query.Filter, query.Format, cancellationToken))
            .WithDisplayName("OData Layer Features Count")
            .WithName("ODataLayerFeaturesCount")
            .WithSummary("Get the count of features for a layer")
            .WithTags("OData")
            .Produces<string>(200, "text/plain")
            .Produces(400)
            .Produces(404);

        var legacyLayerFeaturesCount = endpoints.MapGet("/odata/Features({layerId:int})/$count",
            (HttpContext context,
                int layerId,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesCountAsync(context, layerId, query.Filter, query.Format, cancellationToken))
            .WithDisplayName("OData Layer Features Count (Legacy)")
            .WithName("ODataLayerFeaturesCountLegacy")
            .WithSummary("Legacy count endpoint for layer features")
            .WithTags("OData")
            .Produces<string>(200, "text/plain")
            .Produces(400)
            .Produces(404);

        // Legacy layer-scoped features endpoint
        var legacyLayerFeatures = endpoints.MapGet("/odata/Features({layerId:int})",
            (HttpContext context,
                int layerId,
                [FromServices] ODataStreamingQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeaturesAsync(
                    context,
                    layerId,
                    query.Filter,
                    query.Select,
                    query.Orderby,
                    query.Top,
                    query.Skip,
                    query.Skiptoken,
                    query.Count,
                    query.Expand,
                    query.Compute,
                    query.Search,
                    query.Apply,
                    query.Deltatoken,
                    query.Format,
                    cancellationToken))
            .WithDisplayName("OData Features Collection (Legacy)")
            .WithName("ODataFeaturesLegacy")
            .WithSummary("Legacy layer-scoped features endpoint")
            .WithTags("OData")
            .Produces<Models.ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // POST - Create a new feature (collection)
        var createFeature = endpoints.MapPost("/odata/Features",
            (HttpContext context, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleCreateFeatureAsync(context, null, cancellationToken))
            .WithDisplayName("OData Create Feature")
            .WithName("ODataCreateFeature")
            .WithSummary("Create a new feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(201, "application/json")
            .Produces(400)
            .Produces(404);
        createFeature.AllowAnonymous();

        // POST - Create a new feature for a layer
        var createLayerFeature = endpoints.MapPost("/odata/Layers({layerId:int})/Features",
            (HttpContext context, int layerId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleCreateFeatureAsync(context, layerId, cancellationToken))
            .WithDisplayName("OData Create Feature")
            .WithName("ODataCreateLayerFeature")
            .WithSummary("Create a new feature in the specified layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(201, "application/json")
            .Produces(400)
            .Produces(404);
        createLayerFeature.AllowAnonymous();

        // GET - Get a single feature
        var getFeature = endpoints.MapGet("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})",
            (HttpContext context,
                int layerId,
                long objectId,
                [FromServices] ODataCrudHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetSingleFeatureAsync(context, layerId, objectId, query.Select, query.Format, cancellationToken))
            .WithDisplayName("OData Get Feature")
            .WithName("ODataGetFeature")
            .WithSummary("Get a single feature by ID")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        var getFeatureRef = endpoints.MapGet("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})/$ref",
            (HttpContext context,
                int layerId,
                long objectId,
                [FromServices] ODataCrudHandler handler,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeatureReferenceAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Get Feature Reference")
            .WithName("ODataGetFeatureReference")
            .WithSummary("Get canonical entity reference for a feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        var getFeatureValue = endpoints.MapGet("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})/$value",
            (HttpContext context,
                int layerId,
                long objectId,
                [FromServices] ODataCrudHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetFeatureValueAsync(context, layerId, objectId, query.Format, cancellationToken))
            .WithDisplayName("OData Get Feature Value")
            .WithName("ODataGetFeatureValue")
            .WithSummary("Get the raw JSON value for a feature")
            .WithTags("OData")
            .Produces<string>(200, "application/json")
            .Produces(404);

        var getLayerFeature = endpoints.MapGet("/odata/Layers({layerId:int})/Features({objectId:long})",
            (HttpContext context,
                int layerId,
                long objectId,
                [FromServices] ODataCrudHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetSingleFeatureAsync(context, layerId, objectId, query.Select, query.Format, cancellationToken))
            .WithDisplayName("OData Get Feature (Layer)")
            .WithName("ODataGetLayerFeature")
            .WithSummary("Get a single feature in a layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        // Legacy feature key format
        var legacyGetFeature = endpoints.MapGet("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context,
                int layerId,
                long objectId,
                [FromServices] ODataCrudHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleGetSingleFeatureAsync(context, layerId, objectId, query.Select, query.Format, cancellationToken))
            .WithDisplayName("OData Get Feature (Legacy)")
            .WithName("ODataGetFeatureLegacy")
            .WithSummary("Legacy feature key format")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        // PATCH - Update an existing feature
        var updateFeature = endpoints.MapPatch("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleUpdateFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Update Feature")
            .WithName("ODataUpdateFeature")
            .WithSummary("Update an existing feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        updateFeature.AllowAnonymous();

        var updateLayerFeature = endpoints.MapPatch("/odata/Layers({layerId:int})/Features({objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleUpdateFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Update Feature (Layer)")
            .WithName("ODataUpdateLayerFeature")
            .WithSummary("Update an existing feature in a layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        updateLayerFeature.AllowAnonymous();

        var legacyUpdateFeature = endpoints.MapPatch("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleUpdateFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Update Feature (Legacy)")
            .WithName("ODataUpdateFeatureLegacy")
            .WithSummary("Legacy feature key format")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        legacyUpdateFeature.AllowAnonymous();

        // PUT - Replace an existing feature
        var replaceFeature = endpoints.MapPut("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleReplaceFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Replace Feature")
            .WithName("ODataReplaceFeature")
            .WithSummary("Replace an existing feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        replaceFeature.AllowAnonymous();

        var replaceLayerFeature = endpoints.MapPut("/odata/Layers({layerId:int})/Features({objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleReplaceFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Replace Feature (Layer)")
            .WithName("ODataReplaceLayerFeature")
            .WithSummary("Replace an existing feature in a layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        replaceLayerFeature.AllowAnonymous();

        var legacyReplaceFeature = endpoints.MapPut("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleReplaceFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Replace Feature (Legacy)")
            .WithName("ODataReplaceFeatureLegacy")
            .WithSummary("Legacy feature key format")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        legacyReplaceFeature.AllowAnonymous();

        // DELETE - Delete a feature
        var deleteFeature = endpoints.MapDelete("/odata/Features(LayerId={layerId:int},ObjectId={objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Delete Feature")
            .WithName("ODataDeleteFeature")
            .WithSummary("Delete a feature")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);
        deleteFeature.AllowAnonymous();

        var deleteLayerFeature = endpoints.MapDelete("/odata/Layers({layerId:int})/Features({objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Delete Feature (Layer)")
            .WithName("ODataDeleteLayerFeature")
            .WithSummary("Delete a feature in a layer")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);
        deleteLayerFeature.AllowAnonymous();

        var legacyDeleteFeature = endpoints.MapDelete("/odata/Features({layerId:int},{objectId:long})",
            (HttpContext context, int layerId, long objectId, [FromServices] ODataCrudHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteFeatureAsync(context, layerId, objectId, cancellationToken))
            .WithDisplayName("OData Delete Feature (Legacy)")
            .WithName("ODataDeleteFeatureLegacy")
            .WithSummary("Legacy feature key format")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);
        legacyDeleteFeature.AllowAnonymous();

        // POST - Batch operations
        // The batch handler performs per-operation authorization checks so read-only
        // requests to public layers can succeed while writes remain protected.
        var batch = endpoints.MapPost("/odata/$batch",
            (HttpContext context, [FromServices] ODataBatchOperationHandler handler, CancellationToken cancellationToken) =>
                handler.HandleBatchRequestAsync(context, cancellationToken))
            .WithDisplayName("OData Batch Operations")
            .WithName("ODataBatch")
            .WithSummary("Execute multiple operations in a single request with optional atomicity groups")
            .WithTags("OData")
            .Produces<Models.ODataBatchResponse>(200, "application/json")
            .Produces<string>(200, "multipart/mixed")
            .Produces(400);
        batch.AllowAnonymous();

        // GET - Aggregation with $apply (legacy)
        var apply = endpoints.MapGet("/odata/Features({layerId:int})/$apply",
            (HttpContext context,
                int layerId,
                [FromServices] ODataAdvancedQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleApplyAsync(context, layerId, query.Apply, query.Filter, query.Format, cancellationToken))
            .WithDisplayName("OData Aggregation")
            .WithName("ODataApply")
            .WithSummary("Aggregate features using $apply transformations (aggregate, groupby, filter, compute)")
            .WithTags("OData")
            .Produces<Models.ODataAggregationResult>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // GET - Full-text search with $search (legacy)
        var search = endpoints.MapGet("/odata/Features({layerId:int})/$search",
            (HttpContext context,
                int layerId,
                [FromServices] ODataAdvancedQueryHandler handler,
                [AsParameters] ODataQueryOptions query,
                CancellationToken cancellationToken) =>
                handler.HandleSearchAsync(
                    context,
                    layerId,
                    query.Search,
                    query.Filter,
                    query.Orderby,
                    query.Select,
                    query.Expand,
                    query.Top,
                    query.Skip,
                    query.Count,
                    query.Format,
                    cancellationToken))
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
