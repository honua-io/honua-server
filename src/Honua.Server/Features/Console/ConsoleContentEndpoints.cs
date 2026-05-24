// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Console content item CRUD/list/search endpoints.
/// </summary>
internal static class ConsoleContentEndpoints
{
    private const int DefaultProvenanceDepth = 5;

    /// <summary>
    /// Log category for Console content endpoints.
    /// </summary>
    internal sealed class ConsoleContentEndpointsLog;

    /// <summary>
    /// Maps the Console content endpoints into the supplied builder.
    /// </summary>
    public static void MapConsoleContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/content")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleList)
            .WithDisplayName("List Console Content Items")
            .WithSummary("Lists Console content items with cursor-based pagination and optional filters (type, visibility, owner, namespace).")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreate)
            .WithDisplayName("Create Console Content Item")
            .WithSummary("Creates a new Console content item. Returns the stored item with computed actions for the requesting principal.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/search", HandleSearch)
            .WithDisplayName("Search Console Content Items")
            .WithSummary("Searches content items by free-form term. Returns the same shape as the list endpoint.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{id}", HandleGet)
            .WithDisplayName("Get Console Content Item")
            .WithSummary("Returns a content item by id, including provenance and computed actions.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id}", HandleUpdate)
            .WithDisplayName("Replace Console Content Item")
            .WithSummary("Full replacement update; honours optimistic concurrency via the generation field.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPatch("/{id}", HandlePatch)
            .WithDisplayName("Patch Console Content Item")
            .WithSummary("Partial update for displayable fields (title, description, tags, labels, visibility). Does not touch generation.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Patch }));

        group.MapDelete("/{id}", HandleDelete)
            .WithDisplayName("Delete Console Content Item")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/{id}/provenance", HandleGetProvenance)
            .WithDisplayName("Get Console Content Item Provenance Chain")
            .WithSummary("Resolves the transitive provenance chain anchored on the supplied item id, capped by the depth parameter (default 5).")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleContentListResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleList(
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context,
        [FromQuery] string? itemType = null,
        [FromQuery] string? visibility = null,
        [FromQuery] string? owner = null,
        [FromQuery(Name = "namespace")] string? @namespace = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25,
        [FromQuery] string? q = null)
    {
        try
        {
            if (!TryParseItemTypeList(itemType, out var itemTypes, out var itemTypeError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Invalid itemType filter: {itemTypeError}"));
            }
            if (!TryParseVisibilityList(visibility, out var visibilities, out var visibilityError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Invalid visibility filter: {visibilityError}"));
            }

            var query = new ConsoleContentQuery
            {
                ItemTypes = itemTypes,
                Visibilities = visibilities,
                OwnerId = string.IsNullOrWhiteSpace(owner) ? null : owner,
                Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace,
                Cursor = cursor,
                Limit = limit,
                SearchTerm = string.IsNullOrWhiteSpace(q) ? null : q,
            };

            var result = await store.ListAsync(query, context.RequestAborted).ConfigureAwait(false);
            var items = await ConsoleContentMapper.WithComputedActionsAsync(result.Items, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);

            ConsoleEndpointsLog.ContentItemsListed(logger, items.Count, result.Total);
            return TypedResults.Ok(ApiResponse<ConsoleContentListResponse>.CreateSuccess(new ConsoleContentListResponse
            {
                Items = items,
                Total = result.Total,
                NextCursor = result.NextCursor,
            }));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.list", ex);
            return TypedResults.Problem(
                title: "Console content listing failed",
                detail: "An internal error occurred while listing Console content items.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ConsoleContentListResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleSearch(
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context,
        [FromQuery] string? q = null,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null)
    {
        return HandleList(store, evaluator, logger, context, itemType: null, visibility: null, owner: null, @namespace: null, cursor: cursor, limit: limit, q: q);
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleContentItem>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGet(
        string id,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var item = await store.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var withActions = await ConsoleContentMapper.WithComputedActionsAsync(item, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleContentItem>.CreateSuccess(withActions));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.get", ex);
            return TypedResults.Problem(
                title: "Console content retrieval failed",
                detail: "An internal error occurred while retrieving the Console content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<ConsoleContentItem>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleCreate(
        CreateConsoleContentItemRequest request,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var item = ConsoleContentMapper.FromCreateRequest(request, context.User);
            var stored = await store.CreateAsync(item, context.RequestAborted).ConfigureAwait(false);
            var withActions = await ConsoleContentMapper.WithComputedActionsAsync(stored, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);

            ConsoleEndpointsLog.ContentItemCreated(logger, stored.Id, stored.ItemType);
            return TypedResults.Created($"/api/v1/console/content/{stored.Id}", ApiResponse<ConsoleContentItem>.CreateSuccess(withActions));
        }
        catch (InvalidOperationException ioe)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(ioe.Message));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.create", ex);
            return TypedResults.Problem(
                title: "Console content creation failed",
                detail: "An internal error occurred while creating the Console content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleContentItem>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<object>>, ProblemHttpResult>> HandleUpdate(
        string id,
        UpdateConsoleContentItemRequest request,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var existing = await store.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            if (request.ItemType != existing.ItemType)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("itemType is immutable on update."));
            }

            var projected = ConsoleContentMapper.FromUpdateRequest(id, request, existing, context.User);
            ConsoleContentItem? updated;
            try
            {
                updated = await store.UpdateAsync(projected, context.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidOperationException ioe)
            {
                return TypedResults.Conflict(ApiResponse<object>.Failure(ioe.Message));
            }
            if (updated is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var withActions = await ConsoleContentMapper.WithComputedActionsAsync(updated, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            ConsoleEndpointsLog.ContentItemUpdated(logger, updated.Id, updated.Generation);
            return TypedResults.Ok(ApiResponse<ConsoleContentItem>.CreateSuccess(withActions));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.update", ex);
            return TypedResults.Problem(
                title: "Console content update failed",
                detail: "An internal error occurred while updating the Console content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleContentItem>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandlePatch(
        string id,
        PatchConsoleContentItemRequest request,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var patch = ConsoleContentMapper.FromPatchRequest(request, context.User);
            var updated = await store.PatchAsync(id, patch, context.RequestAborted).ConfigureAwait(false);
            if (updated is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var withActions = await ConsoleContentMapper.WithComputedActionsAsync(updated, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            ConsoleEndpointsLog.ContentItemUpdated(logger, updated.Id, updated.Generation);
            return TypedResults.Ok(ApiResponse<ConsoleContentItem>.CreateSuccess(withActions));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.patch", ex);
            return TypedResults.Problem(
                title: "Console content patch failed",
                detail: "An internal error occurred while patching the Console content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleDelete(
        string id,
        [FromServices] IConsoleContentStore store,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var deleted = await store.DeleteAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (!deleted)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            ConsoleEndpointsLog.ContentItemDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Console content item deleted."));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.delete", ex);
            return TypedResults.Problem(
                title: "Console content deletion failed",
                detail: "An internal error occurred while deleting the Console content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleProvenanceChainResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetProvenance(
        string id,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleContentEndpointsLog> logger,
        HttpContext context,
        [FromQuery] int depth = DefaultProvenanceDepth)
    {
        try
        {
            var maxDepth = Math.Clamp(depth, 1, DefaultProvenanceDepth);
            var chain = await store.GetProvenanceChainAsync(id, maxDepth, context.RequestAborted).ConfigureAwait(false);
            if (chain.Count == 0)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var chainWithActions = await ConsoleContentMapper.WithComputedActionsAsync(chain, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleProvenanceChainResponse>.CreateSuccess(new ConsoleProvenanceChainResponse
            {
                ItemId = id,
                Chain = chainWithActions,
                MaxDepth = maxDepth,
            }));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "content.provenance", ex);
            return TypedResults.Problem(
                title: "Console provenance lookup failed",
                detail: "An internal error occurred while resolving the provenance chain.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryValidate<TRequest>(TRequest request, out string error) where TRequest : notnull
    {
        var validationResults = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }
        error = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
        return false;
    }

    private static bool TryParseItemTypeList(string? value, out IReadOnlyList<ConsoleContentItemType>? values, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values = null;
            error = string.Empty;
            return true;
        }

        var parsed = new List<ConsoleContentItemType>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ConsoleEnumParser.TryParse(raw, out ConsoleContentItemType member))
            {
                values = null;
                error = $"unknown value '{raw}'";
                return false;
            }
            parsed.Add(member);
        }
        values = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryParseVisibilityList(string? value, out IReadOnlyList<ConsoleVisibility>? values, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values = null;
            error = string.Empty;
            return true;
        }

        var parsed = new List<ConsoleVisibility>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ConsoleEnumParser.TryParse(raw, out ConsoleVisibility member))
            {
                values = null;
                error = $"unknown value '{raw}'";
                return false;
            }
            parsed.Add(member);
        }
        values = parsed;
        error = string.Empty;
        return true;
    }
}
