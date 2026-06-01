// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Anonymous open-data read surface: DCAT/data.json, the STAC
/// catalog/collection/item projections, and the Schema.org Dataset JSON-LD
/// projection. These routes are intentionally public but enforce strict policy:
/// a dataset is only readable when the item is open-data <em>eligible</em>
/// (public-indexed distributable type) and, for STAC, currently
/// <em>published</em>. Private, protected, ineligible, missing, and not-published
/// resources all return an identical 404 that never discloses item identity or
/// titles.
/// </summary>
internal static class ConsoleOpenDataPublicEndpoints
{
    private const string OpenDataDeniedMessage = "Open-data dataset not found.";
    private const string StacBasePath = "/api/v1/open-data/stac";

    /// <summary>Log category for the anonymous Console open-data endpoints.</summary>
    internal sealed class ConsoleOpenDataPublicEndpointsLogCategory;

    /// <summary>Maps the anonymous Console open-data endpoints.</summary>
    public static void MapConsoleOpenDataPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous is deliberate: open-data reads are public, but every
        // handler re-checks eligibility/publication before disclosing anything.
        // NoCache is required: eligibility and STAC publication state are mutable
        // and policy-gated, so the anonymous-only base output-cache policy must not
        // serve a stale 200 after an item is unpublished or its tier is reverted.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/open-data")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .AllowAnonymous()
            .CacheOutput(policy => policy.NoCache());

        group.MapGet("/datasets/{id}", HandleGetDataset)
            .WithDisplayName("Read Anonymous Open-Data Dataset")
            .WithSummary("Returns the anonymous open-data page projection for a public-indexed item. Private/ineligible/missing items return 404 without leaking titles.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/datasets/{id}/data.json", HandleGetDataJson)
            .WithDisplayName("Read Anonymous Open-Data data.json")
            .WithSummary("Returns the DCAT-US 3.0/data.json catalog for a public-indexed item.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/datasets/{id}/schema.org", HandleGetSchemaOrg)
            .WithDisplayName("Read Anonymous Open-Data Schema.org Dataset")
            .WithSummary("Returns the Schema.org Dataset JSON-LD projection for a public-indexed item.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/stac", HandleGetStacCatalog)
            .WithDisplayName("Read Anonymous Open-Data STAC Catalog")
            .WithSummary("Returns the STAC catalog root over all currently-published open-data items.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/stac/collections/{collectionId}", HandleGetStacCollection)
            .WithDisplayName("Read Anonymous Open-Data STAC Collection")
            .WithSummary("Returns the STAC collection for a published item. Unpublished/ineligible/missing items return 404.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/stac/collections/{collectionId}/items/{itemId}", HandleGetStacItem)
            .WithDisplayName("Read Anonymous Open-Data STAC Item")
            .WithSummary("Returns the representative STAC item for a published collection. Unpublished/ineligible/missing return 404.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleOpenDataPage>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetDataset(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolved = await ResolveEligibleAsync(id, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            ConsoleOpenDataEndpointsLog.PublicReadGranted(logger, id);
            return TypedResults.Ok(ApiResponse<ConsoleOpenDataPage>.CreateSuccess(resolved.Value.Page));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.dataset", ex);
            return AnonymousError();
        }
    }

    private static async Task<Results<Ok<DcatCatalog>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetDataJson(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolved = await ResolveEligibleAsync(id, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            ConsoleOpenDataEndpointsLog.PublicReadGranted(logger, id);
            // data.json is a standard document, not wrapped in the API envelope.
            return TypedResults.Ok(ConsoleOpenDataMapper.BuildDcatCatalog(resolved.Value.Page));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.data-json", ex);
            return AnonymousError();
        }
    }

    private static async Task<Results<Ok<SchemaOrgDataset>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetSchemaOrg(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolved = await ResolveEligibleAsync(id, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            ConsoleOpenDataEndpointsLog.PublicReadGranted(logger, id);
            return TypedResults.Ok(ConsoleOpenDataMapper.BuildSchemaOrgDataset(resolved.Value.Page));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.schema-org", ex);
            return AnonymousError();
        }
    }

    private static async Task<Results<Ok<StacProjectionCatalog>, ProblemHttpResult>> HandleGetStacCatalog(
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var ids = await openDataStore.ListPublishedStacItemIdsAsync(context.RequestAborted).ConfigureAwait(false);
            var published = new List<(ConsoleStacPublicationState, ConsoleOpenDataPage)>();
            foreach (var itemId in ids)
            {
                // Re-check eligibility per item: a previously-published item whose
                // tier was reverted must drop out of the catalog root.
                var resolved = await ResolveEligibleAsync(itemId, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
                if (resolved is { } r && r.Publication.Status == ConsoleStacPublicationStatus.Published)
                {
                    published.Add((r.Publication, r.Page));
                }
            }

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacCatalog(published, StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.stac.catalog", ex);
            return TypedResults.Problem(
                title: "Console STAC catalog read failed",
                detail: "An internal error occurred while reading the STAC catalog.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<StacProjectionCollection>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetStacCollection(
        string collectionId,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolved = await ResolvePublishedCollectionAsync(collectionId, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacCollection(resolved.Value.Page, resolved.Value.Publication, StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.stac.collection", ex);
            return AnonymousError();
        }
    }

    private static async Task<Results<Ok<StacProjectionItem>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetStacItem(
        string collectionId,
        string itemId,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            // Honua emits one representative item per collection (id == collectionId).
            if (!string.Equals(collectionId, itemId, StringComparison.Ordinal))
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            var resolved = await ResolvePublishedCollectionAsync(collectionId, contentStore, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                ConsoleOpenDataEndpointsLog.PublicReadDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(OpenDataDeniedMessage));
            }

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacItem(resolved.Value.Page, resolved.Value.Publication, StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.stac.item", ex);
            return AnonymousError();
        }
    }

    /// <summary>
    /// Resolves an item for anonymous open-data reads: returns the eligible page +
    /// publication state, or null when the item is missing or not open-data
    /// eligible (private/protected/ineligible). Callers apply uniform denial.
    /// </summary>
    private static async Task<(ConsoleOpenDataPage Page, ConsoleStacPublicationState Publication)?> ResolveEligibleAsync(
        string id,
        IConsoleContentStore contentStore,
        IConsoleShareStore shareStore,
        IConsoleOpenDataStore openDataStore,
        CancellationToken cancellationToken)
    {
        var item = await contentStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var shareState = await shareStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var tier = ConsoleShareMapper.ResolveEffectiveTier(shareState, item);
        if (!ConsoleOpenDataMapper.IsDistributableType(item.ItemType) || tier != ConsoleShareAccessTier.PublicIndexed)
        {
            return null;
        }

        var stored = await openDataStore.GetPageAsync(id, cancellationToken).ConfigureAwait(false);
        var page = ConsoleOpenDataMapper.BuildPageProjection(item, stored);
        var publication = await openDataStore.GetStacPublicationAsync(id, cancellationToken).ConfigureAwait(false);
        return (page, publication);
    }

    /// <summary>
    /// Resolves a STAC collection id to its eligible, currently-published item.
    /// The collection id is the store-assigned id; this maps it back to the item.
    /// </summary>
    private static async Task<(ConsoleOpenDataPage Page, ConsoleStacPublicationState Publication)?> ResolvePublishedCollectionAsync(
        string collectionId,
        IConsoleContentStore contentStore,
        IConsoleShareStore shareStore,
        IConsoleOpenDataStore openDataStore,
        CancellationToken cancellationToken)
    {
        var publishedIds = await openDataStore.ListPublishedStacItemIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var itemId in publishedIds)
        {
            var publication = await openDataStore.GetStacPublicationAsync(itemId, cancellationToken).ConfigureAwait(false);
            var resolvedCollectionId = publication.CollectionId ?? itemId;
            if (!string.Equals(resolvedCollectionId, collectionId, StringComparison.Ordinal))
            {
                continue;
            }

            var resolved = await ResolveEligibleAsync(itemId, contentStore, shareStore, openDataStore, cancellationToken).ConfigureAwait(false);
            if (resolved is { } r && r.Publication.Status == ConsoleStacPublicationStatus.Published)
            {
                return r;
            }

            // Found by id but no longer eligible/published — deny without disclosing.
            return null;
        }

        return null;
    }

    private static ProblemHttpResult AnonymousError()
        => TypedResults.Problem(
            title: "Console open-data read failed",
            detail: "An internal error occurred while reading the open-data resource.",
            statusCode: StatusCodes.Status500InternalServerError);
}
