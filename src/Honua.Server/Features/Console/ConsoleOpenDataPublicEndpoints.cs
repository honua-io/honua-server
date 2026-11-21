// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

// Unknown, ineligible, unpublished, and missing resources all return the same 404 and never disclose identity or titles.
internal static class ConsoleOpenDataPublicEndpoints
{
    private const string OpenDataDeniedMessage = "Open-data dataset not found.";
    private const string StacBasePath = "/api/v1/open-data/stac";
    private const int DefaultListLimit = 25;
    private const int MaxListLimit = 50;
    private const int CandidateBatchSize = 200;
    private static readonly ConsoleContentItemType[] DistributableTypes =
        [ConsoleContentItemType.Layer, ConsoleContentItemType.Service, ConsoleContentItemType.OpenData];

    internal sealed class ConsoleOpenDataPublicEndpointsLogCategory;

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

        group.MapGet("/datasets", HandleListDatasets)
            .WithDisplayName("List Anonymous Open-Data Datasets")
            .WithSummary("Lists the anonymous open-data page projections of public-indexed items, one bounded page per request. Private and ineligible items are never listed.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

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
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // dataset endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.dataset", ex);
            return AnonymousError();
        }
    }

    // Bound from the request: the three stores the eligibility check reads, plus the paging query.
    internal sealed class DatasetListRequest
    {
        [FromServices]
        public IConsoleContentStore ContentStore { get; init; } = null!;

        [FromServices]
        public IConsoleShareStore ShareStore { get; init; } = null!;

        [FromServices]
        public IConsoleOpenDataStore OpenDataStore { get; init; } = null!;

        [FromQuery(Name = "cursor")]
        public string? Cursor { get; init; }

        [FromQuery(Name = "limit")]
        public int? Limit { get; init; }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleOpenDataDatasetListResponse>>, ProblemHttpResult>> HandleListDatasets(
        [AsParameters] DatasetListRequest request,
        [FromServices] ILogger<ConsoleOpenDataPublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var list = await ListEligibleDatasetsAsync(request, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleOpenDataDatasetListResponse>.CreateSuccess(list));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // dataset list endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.datasets", ex);
            return AnonymousError();
        }
    }

    // Keyset paging over eligible items in id order: the cursor is the id of the last item
    // returned, which the reader can already see, so paging never reveals how many public
    // items are hidden from them. No total is returned for the same reason.
    private static async Task<ConsoleOpenDataDatasetListResponse> ListEligibleDatasetsAsync(
        DatasetListRequest request,
        CancellationToken cancellationToken)
    {
        var limit = request.Limit is > 0 ? Math.Min(request.Limit.Value, MaxListLimit) : DefaultListLimit;
        var entries = new List<ConsoleOpenDataDatasetListItem>(limit);
        await foreach (var candidate in CandidatesAfterAsync(request.ContentStore, DecodeCursor(request.Cursor), cancellationToken).ConfigureAwait(false))
        {
            var page = await ProjectIfEligibleAsync(request, candidate, cancellationToken).ConfigureAwait(false);
            if (page is not null)
            {
                entries.Add(new ConsoleOpenDataDatasetListItem { ItemType = candidate.ItemType, Page = page });
                if (entries.Count == limit)
                {
                    break;
                }
            }
        }

        return new ConsoleOpenDataDatasetListResponse
        {
            Items = entries,
            NextCursor = entries.Count == limit ? EncodeCursor(entries[^1].Page.ItemId) : null,
        };
    }

    // Public items of a distributable type, in id order, after the given id. Only these can be eligible.
    private static async IAsyncEnumerable<ConsoleContentItem> CandidatesAfterAsync(
        IConsoleContentStore contentStore,
        string? afterId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? storeCursor = null;
        do
        {
            var batch = await contentStore.ListAsync(new ConsoleContentQuery
            {
                ItemTypes = DistributableTypes,
                Visibilities = [ConsoleVisibility.Public],
                Cursor = storeCursor,
                Limit = CandidateBatchSize,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in batch.Items.Where(item => afterId is null || string.CompareOrdinal(item.Id, afterId) > 0))
            {
                yield return item;
            }

            storeCursor = batch.NextCursor;
        }
        while (storeCursor is not null);
    }

    private static async Task<ConsoleOpenDataPage?> ProjectIfEligibleAsync(
        DatasetListRequest request,
        ConsoleContentItem item,
        CancellationToken cancellationToken)
    {
        var shareState = await request.ShareStore.GetAsync(item.Id, cancellationToken).ConfigureAwait(false);
        if (!IsOpenDataEligible(item, shareState))
        {
            return null;
        }

        var stored = await request.OpenDataStore.GetPageAsync(item.Id, cancellationToken).ConfigureAwait(false);
        return ConsoleOpenDataMapper.BuildPageProjection(item, stored);
    }

    private static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            // A malformed cursor reads from the start, like the other Console list cursors.
            return null;
        }
    }

    private static string EncodeCursor(string itemId) => Convert.ToBase64String(Encoding.UTF8.GetBytes(itemId));

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
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // data.json endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // Schema.org endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacCatalog(published, BaseUrlResolver.GetBaseUrl(context) + StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // STAC catalog endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacCollection(resolved.Value.Page, resolved.Value.Publication, BaseUrlResolver.GetBaseUrl(context) + StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // STAC collection endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            return TypedResults.Ok(ConsoleOpenDataMapper.BuildStacItem(resolved.Value.Page, resolved.Value.Publication, BaseUrlResolver.GetBaseUrl(context) + StacBasePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: this is the request-handling boundary for the anonymous
        // STAC item endpoint; the failure is logged and mapped to a generic error response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.public.stac.item", ex);
            return AnonymousError();
        }
    }

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
        if (!IsOpenDataEligible(item, shareState))
        {
            return null;
        }

        var stored = await openDataStore.GetPageAsync(id, cancellationToken).ConfigureAwait(false);
        var page = ConsoleOpenDataMapper.BuildPageProjection(item, stored);
        var publication = await openDataStore.GetStacPublicationAsync(id, cancellationToken).ConfigureAwait(false);
        return (page, publication);
    }

    // The one eligibility rule for every anonymous open-data read: a distributable type whose
    // effective Share tier is public-indexed.
    private static bool IsOpenDataEligible(ConsoleContentItem item, ConsoleShareState? shareState)
        => ConsoleOpenDataMapper.IsDistributableType(item.ItemType)
            && ConsoleShareMapper.ResolveEffectiveTier(shareState, item) == ConsoleShareAccessTier.PublicIndexed;

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
