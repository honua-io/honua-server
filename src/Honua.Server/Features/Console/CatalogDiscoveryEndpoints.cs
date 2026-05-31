// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Console catalog discovery-endpoints registry read API (honua-server#1279).
/// Workspace-scoped admin reads describing which discovery dialects (Esri
/// catalog, OGC API Records, OData, STAC, DCAT) the server publishes to
/// consumers, each endpoint's on/off and auto-default-vs-opt-in state, its
/// feeders, and the per-endpoint/per-item drill-down. Distinct from the
/// singular content catalog (<c>/api/v1/console/content</c>, #1162).
/// </summary>
internal static class CatalogDiscoveryEndpoints
{
    /// <summary>Log category for catalog discovery endpoints.</summary>
    internal sealed class CatalogDiscoveryEndpointsLog;

    /// <summary>Maps the catalog discovery-endpoints registry read endpoints.</summary>
    public static void MapCatalogDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/catalog-endpoints")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/{workspaceId}", HandleGetRegistry)
            .WithDisplayName("Get Catalog Discovery Registry")
            .WithSummary("Returns the workspace's catalog discovery-endpoints registry: the set of discovery dialects the server publishes, each endpoint's on/off and auto-default-vs-opt-in state, feeders, and aggregate counts.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{workspaceId}/{endpointKey}", HandleGetEndpoint)
            .WithDisplayName("Get Catalog Discovery Endpoint Detail")
            .WithSummary("Returns one discovery endpoint with its header facts and mirrored items table.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{workspaceId}/{endpointKey}/items/{itemId}", HandleGetItem)
            .WithDisplayName("Get Catalog Discovery Item")
            .WithSummary("Returns one catalog item with its identity, catalog-presentation, service-binding, and standards-mapping field groups.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<CatalogDiscoveryRegistry>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetRegistry(
        string workspaceId,
        [FromServices] ICatalogDiscoveryRegistryStore store,
        [FromServices] ILogger<CatalogDiscoveryEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var registry = await store.GetRegistryAsync(workspaceId, context.RequestAborted).ConfigureAwait(false);
            if (registry is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Catalog discovery registry not found for workspace."));
            }

            return TypedResults.Ok(ApiResponse<CatalogDiscoveryRegistry>.CreateSuccess(registry));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "catalog-endpoints.registry", ex);
            return TypedResults.Problem(
                title: "Catalog discovery registry lookup failed",
                detail: "An internal error occurred while reading the catalog discovery registry.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<CatalogEndpointDetail>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetEndpoint(
        string workspaceId,
        string endpointKey,
        [FromServices] ICatalogDiscoveryRegistryStore store,
        [FromServices] ILogger<CatalogDiscoveryEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var detail = await store.GetEndpointAsync(workspaceId, endpointKey, context.RequestAborted).ConfigureAwait(false);
            if (detail is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Catalog discovery endpoint not found."));
            }

            return TypedResults.Ok(ApiResponse<CatalogEndpointDetail>.CreateSuccess(detail));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "catalog-endpoints.detail", ex);
            return TypedResults.Problem(
                title: "Catalog discovery endpoint lookup failed",
                detail: "An internal error occurred while reading the catalog discovery endpoint.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<CatalogItem>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetItem(
        string workspaceId,
        string endpointKey,
        string itemId,
        [FromServices] ICatalogDiscoveryRegistryStore store,
        [FromServices] ILogger<CatalogDiscoveryEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var item = await store.GetItemAsync(workspaceId, endpointKey, itemId, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Catalog discovery item not found."));
            }

            return TypedResults.Ok(ApiResponse<CatalogItem>.CreateSuccess(item));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "catalog-endpoints.item", ex);
            return TypedResults.Problem(
                title: "Catalog discovery item lookup failed",
                detail: "An internal error occurred while reading the catalog discovery item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
