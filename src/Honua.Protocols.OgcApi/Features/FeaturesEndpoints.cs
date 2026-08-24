// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Diagnostics;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Api.Features;

/// <summary>
/// Features/Items CRUD endpoints for OGC API Features.
/// Refactored to delegate to focused handlers for better maintainability.
/// </summary>
internal static partial class FeaturesEndpoints
{
    private static readonly string[] _patchMethods = ["PATCH"];

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
            .Produces<string>(200, MediaTypes.Csv)
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
            .Produces<string>(200, MediaTypes.Csv)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404)
            .Produces(400);

        endpoints.MapPost("/ogc/features/collections/{collectionId}/items", HandleCreateFeature)
            .WithDisplayName("OGC API Features Create Item")
            .WithName("CreateItem")
            .WithSummary("Create a new feature")
            .WithDescription("Add a new feature to the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson, MediaTypes.Json)
            .Produces<GeoJsonFeature>(201, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .Produces(409)
            .AllowAnonymous();

        endpoints.MapPut("/ogc/features/collections/{collectionId}/items/{featureId}", HandleUpdateFeature)
            .WithDisplayName("OGC API Features Update Item")
            .WithName("UpdateItem")
            .WithSummary("Update a feature")
            .WithDescription("Replace an existing feature with new data")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson, MediaTypes.Json)
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .AllowAnonymous();

        endpoints.MapMethods(
                "/ogc/features/collections/{collectionId}/items/{featureId}",
                _patchMethods,
                HandlePatchFeature)
            .WithDisplayName("OGC API Features Patch Item")
            .WithName("PatchItem")
            .WithSummary("Partially update a feature")
            .WithDescription("Apply a partial update to an existing feature")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Accepts<JsonElement>(MediaTypes.Json)
            .Accepts<JsonElement>("application/merge-patch+json")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .AllowAnonymous();

        endpoints.MapDelete("/ogc/features/collections/{collectionId}/items/{featureId}", HandleDeleteFeature)
            .WithDisplayName("OGC API Features Delete Item")
            .WithName("DeleteItem")
            .WithSummary("Delete a feature")
            .WithDescription("Remove a feature from the collection")
            .WithTags("OGC API Features", "Transactions")
            .Produces(204)
            .Produces(404)
            .AllowAnonymous();

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
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Handles GetItems request by delegating to the query handler.
    /// </summary>
    private static async Task<IResult> HandleGetItems(
        string collectionId,
        HttpContext context,
        string? f,
        string? limit,
        string? offset,
        string? bbox,
        string? datetime,
        string? filter,
        string? ids,
        string? properties,
        string? sortby,
        string? crs,
        OgcFeaturesQueryHandler queryHandler)
    {
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

        // `limit` and `offset` are bound as strings and parsed here rather than
        // declared as `int?`. Minimal-API model binding answers an unparseable
        // simple-type parameter with a bare 400 and an EMPTY body, which is the
        // one invalid-parameter path on this endpoint that did not produce an
        // application/problem+json document (`limit=0`, `offset=-5`,
        // `bbox=notanumber` and `crs=bogus` all do). Parsing at the adapter
        // boundary keeps every rejection on the same structured error surface.
        // The published contract is unchanged: the parameters remain integers
        // in src/Honua.Server/openapi.json and non-integers are still rejected
        // with 400.
        if (!TryParseIntegerQueryParameter(limit, "limit", out var parsedLimit, out var limitError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, limitError!);
        }

        if (!TryParseIntegerQueryParameter(offset, "offset", out var parsedOffset, out var offsetError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, offsetError!);
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await queryHandler.HandleGetItemsAsync(
            collectionId, context, f, parsedLimit, parsedOffset, bbox, datetime, filter, ids, properties, sortby, crs, cancellationToken);
    }

    /// <summary>
    /// Parses an optional integer query parameter, mapping an unparseable value
    /// to a structured error message instead of an empty framework response.
    /// </summary>
    /// <remarks>
    /// A repeated query parameter (<c>?limit=3&amp;limit=5</c>) binds as the
    /// comma-joined <see cref="Microsoft.Extensions.Primitives.StringValues"/>
    /// text and therefore also lands here as an explicit rejection rather than
    /// an ambiguous silent pick.
    /// </remarks>
    private static bool TryParseIntegerQueryParameter(
        string? raw,
        string parameterName,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"{parameterName} must be a valid integer.";
        return false;
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
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

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
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

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
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        return await transactionHandler.HandleReplaceFeatureAsync(collectionId, featureId, ifMatch, context, cancellationToken);
    }

    /// <summary>
    /// Handles PatchFeature request by delegating to the transaction handler.
    /// </summary>
    private static async Task<IResult> HandlePatchFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        OgcFeaturesTransactionHandler transactionHandler)
    {
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        return await transactionHandler.HandlePatchFeatureAsync(collectionId, featureId, ifMatch, context, cancellationToken);
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
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        return await crudHandler.HandleDeleteFeatureAsync(collectionId, featureId, ifMatch, context, cancellationToken);
    }

    /// <summary>
    /// Handles batch operations request by delegating to the transaction handler.
    /// </summary>
    private static async Task<IResult> HandleBatchOperation(
        string collectionId,
        HttpContext context,
        OgcFeaturesTransactionHandler transactionHandler)
    {
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await transactionHandler.HandleBatchOperationAsync(collectionId, context, cancellationToken);
    }

    private static bool TryRequireTenantContext(HttpContext context, out IResult? error)
    {
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext is not null && tenantContext.RequireTenantId(out var tenantId, out _))
        {
            Activity.Current?.SetTag("honua.tenant_id", tenantId);
            error = null;
            return true;
        }

        error = StandardErrorHelpers.CreateForbidden(
            context,
            "Tenant context is required to query collection items.");
        return false;
    }

}
