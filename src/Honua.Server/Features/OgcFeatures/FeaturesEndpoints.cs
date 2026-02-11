// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Features/Items CRUD endpoints for OGC API Features.
/// Refactored to delegate to focused handlers for better maintainability.
/// </summary>
internal static partial class FeaturesEndpoints
{
    /// <summary>
    /// Maps features/items CRUD endpoints with delegation to specialized handlers.
    /// </summary>
    public static IEndpointRouteBuilder MapFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Features Items")
            .WithName("ItemsList")
            .WithSummary("Get features from a collection")
            .WithDescription("Get features from a collection with optional filtering using CQL2-Text")
            .WithTags("OGC API Features")
            .Produces<FeatureCollection>(200, MediaTypes.GeoJson)
            .Produces<FeatureCollection>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items/{featureId}", HandleGetItem)
            .WithDisplayName("OGC API Features Item")
            .WithName("ItemById")
            .WithSummary("Get a feature by ID")
            .WithDescription("Get a specific feature by its ID from a collection")
            .WithTags("OGC API Features")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404)
            .Produces(400);

        endpoints.MapPost("/ogc/features/collections/{collectionId}/items", HandleCreateFeature)
            .WithDisplayName("OGC API Features Create Item")
            .WithName("CreateItem")
            .WithSummary("Create a new feature")
            .WithDescription("Add a new feature to the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(201, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .Produces(409)
            .RequireAuthorization();

        endpoints.MapPut("/ogc/features/collections/{collectionId}/items/{featureId}", HandleUpdateFeature)
            .WithDisplayName("OGC API Features Update Item")
            .WithName("UpdateItem")
            .WithSummary("Update a feature")
            .WithDescription("Replace an existing feature with new data")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .RequireAuthorization();

        endpoints.MapDelete("/ogc/features/collections/{collectionId}/items/{featureId}", HandleDeleteFeature)
            .WithDisplayName("OGC API Features Delete Item")
            .WithName("DeleteItem")
            .WithSummary("Delete a feature")
            .WithDescription("Remove a feature from the collection")
            .WithTags("OGC API Features", "Transactions")
            .Produces(204)
            .Produces(404)
            .RequireAuthorization();

        // Additional endpoints for advanced features
        endpoints.MapPost("/ogc/features/collections/{collectionId}/items/batch", HandleBatchOperation)
            .WithDisplayName("OGC API Features Batch Operations")
            .WithName("BatchOperations")
            .WithSummary("Execute batch feature operations")
            .WithDescription("Perform multiple create, update, or delete operations in a single request")
            .WithTags("OGC API Features", "Transactions", "Batch")
            .Accepts<BatchRequest>(MediaTypes.Json)
            .Produces<BatchOperationResponse>(200, MediaTypes.Json)
            .Produces<BatchOperationResponse>(207, MediaTypes.Json) // Multi-Status
            .Produces(400)
            .Produces(404)
            .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Handles GetItems request by delegating to the query handler.
    /// </summary>
    private static async Task<IResult> HandleGetItems(
        string collectionId,
        HttpContext context,
        string? f,
        int? limit,
        int? offset,
        string? bbox,
        string? datetime,
        string? filter,
        string? crs,
        OgcFeaturesQueryHandler queryHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await queryHandler.HandleGetItemsAsync(
            collectionId, context, f, limit, offset, bbox, datetime, filter, crs, cancellationToken);
    }

    /// <summary>
    /// Handles GetItem request by delegating to the query handler.
    /// </summary>
    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string featureId,
        HttpContext context,
        string? f,
        string? crs,
        OgcFeaturesQueryHandler queryHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await queryHandler.HandleGetItemAsync(
            collectionId, featureId, context, f, crs, cancellationToken);
    }

    /// <summary>
    /// Handles CreateFeature request by delegating to the CRUD handler.
    /// </summary>
    private static async Task<IResult> HandleCreateFeature(
        string collectionId,
        HttpContext context,
        OgcFeaturesCrudHandler crudHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await crudHandler.HandleCreateFeatureAsync(collectionId, context, cancellationToken);
    }

    /// <summary>
    /// Handles UpdateFeature request by delegating to the transaction handler.
    /// </summary>
    private static async Task<IResult> HandleUpdateFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        OgcFeaturesTransactionHandler transactionHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        return await transactionHandler.HandleReplaceFeatureAsync(collectionId, featureId, ifMatch, context, cancellationToken);
    }

    /// <summary>
    /// Handles DeleteFeature request by delegating to the CRUD handler.
    /// </summary>
    private static async Task<IResult> HandleDeleteFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        OgcFeaturesCrudHandler crudHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await crudHandler.HandleDeleteFeatureAsync(collectionId, featureId, context, cancellationToken);
    }

    /// <summary>
    /// Handles batch operations request by delegating to the transaction handler.
    /// </summary>
    private static async Task<IResult> HandleBatchOperation(
        string collectionId,
        HttpContext context,
        OgcFeaturesTransactionHandler transactionHandler)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await transactionHandler.HandleBatchOperationAsync(collectionId, context, cancellationToken);
    }

}
