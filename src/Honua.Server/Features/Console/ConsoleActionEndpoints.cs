// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Console batch action-check endpoint for route-level entitlement gates and
/// item-action evaluation when the item is not yet loaded by the client.
/// </summary>
internal static class ConsoleActionEndpoints
{
    /// <summary>
    /// Log category for the action-check endpoint.
    /// </summary>
    internal sealed class ConsoleActionEndpointsLog;

    private static readonly IReadOnlyList<ConsoleContentAction> AllActions =
    [
        ConsoleContentAction.View,
        ConsoleContentAction.Edit,
        ConsoleContentAction.Publish,
        ConsoleContentAction.Share,
        ConsoleContentAction.Embed,
        ConsoleContentAction.Operate,
        ConsoleContentAction.Administer,
    ];

    /// <summary>
    /// Maps the action-check endpoint into the supplied builder.
    /// </summary>
    public static void MapConsoleActionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/actions")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapPost("/check", HandleCheck)
            .WithDisplayName("Check Console Actions")
            .WithSummary("Bulk evaluates whether the requesting principal may perform the supplied actions on a set of content items and/or navigation routes.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleActionCheckResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleCheck(
        ConsoleActionCheckRequest request,
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleActionEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            if (request.Targets is null || request.Targets.Count == 0)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("At least one target is required."));
            }

            var actions = request.Actions is { Count: > 0 } ? request.Actions : AllActions;

            var itemIds = new List<string>();
            var routeKeys = new List<string>();
            foreach (var target in request.Targets)
            {
                if (!string.IsNullOrWhiteSpace(target.ItemId))
                    itemIds.Add(target.ItemId);
                else if (!string.IsNullOrWhiteSpace(target.RouteKey))
                    routeKeys.Add(target.RouteKey);
                else
                    return TypedResults.BadRequest(ApiResponse<object>.Failure("Each target must specify itemId or routeKey."));
            }

            Dictionary<string, ConsoleContentItem?> itemLookup = new(StringComparer.Ordinal);
            foreach (var id in itemIds.Distinct(StringComparer.Ordinal))
            {
                itemLookup[id] = await store.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            }

            var loadedItems = itemLookup.Values.Where(static i => i is not null).Select(static i => i!).ToList();
            IReadOnlyDictionary<string, IReadOnlyList<ConsoleContentAction>> itemActions = loadedItems.Count > 0
                ? await evaluator.EvaluateItemActionsAsync(context.User, loadedItems, actions, context.RequestAborted).ConfigureAwait(false)
                : new Dictionary<string, IReadOnlyList<ConsoleContentAction>>(StringComparer.Ordinal);

            IReadOnlyList<ConsoleNavigationEntitlement> routeEntitlements = routeKeys.Count > 0
                ? await evaluator.EvaluateRouteEntitlementsAsync(context.User, routeKeys.Distinct(StringComparer.Ordinal).ToList(), context.RequestAborted).ConfigureAwait(false)
                : Array.Empty<ConsoleNavigationEntitlement>();

            var entitlementsByRoute = new Dictionary<string, ConsoleNavigationEntitlement>(StringComparer.Ordinal);
            foreach (var entitlement in routeEntitlements)
            {
                entitlementsByRoute[entitlement.RouteKey] = entitlement;
            }

            var results = new List<ConsoleActionCheckResult>(request.Targets.Count);
            foreach (var target in request.Targets)
            {
                if (!string.IsNullOrWhiteSpace(target.ItemId))
                {
                    if (itemLookup.TryGetValue(target.ItemId, out var item) && item is not null)
                    {
                        var allowed = itemActions.TryGetValue(target.ItemId, out var resolved) ? resolved : Array.Empty<ConsoleContentAction>();
                        var denied = ComputeDenied(actions, allowed);
                        results.Add(new ConsoleActionCheckResult
                        {
                            ItemId = target.ItemId,
                            Allowed = allowed,
                            Denied = denied,
                        });
                    }
                    else
                    {
                        results.Add(new ConsoleActionCheckResult
                        {
                            ItemId = target.ItemId,
                            Allowed = Array.Empty<ConsoleContentAction>(),
                            Denied = Array.Empty<ConsoleContentAction>(),
                            NotFound = true,
                        });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(target.RouteKey))
                {
                    // Route entitlements model navigation access; map an "allowed"
                    // entitlement onto View, but only surface it when the caller
                    // asked for View. Other requested verbs are reported as
                    // denied because routes do not carry item verbs.
                    var routeAllowed = entitlementsByRoute.TryGetValue(target.RouteKey, out var entitlement)
                        && entitlement.Allowed
                        && actions.Contains(ConsoleContentAction.View)
                        ? new[] { ConsoleContentAction.View }
                        : Array.Empty<ConsoleContentAction>();
                    var denied = ComputeDenied(actions, routeAllowed);
                    results.Add(new ConsoleActionCheckResult
                    {
                        RouteKey = target.RouteKey,
                        Allowed = routeAllowed,
                        Denied = denied,
                    });
                }
            }

            ConsoleEndpointsLog.ActionCheckEvaluated(logger, request.Targets.Count, itemIds.Count, routeKeys.Count);
            return TypedResults.Ok(ApiResponse<ConsoleActionCheckResponse>.CreateSuccess(new ConsoleActionCheckResponse
            {
                Results = results,
            }));
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "actions.check", ex);
            return TypedResults.Problem(
                title: "Console action check failed",
                detail: "An internal error occurred while evaluating Console actions.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IReadOnlyList<ConsoleContentAction> ComputeDenied(IReadOnlyList<ConsoleContentAction> candidates, IReadOnlyList<ConsoleContentAction> allowed)
    {
        if (candidates.Count == 0)
            return Array.Empty<ConsoleContentAction>();

        var allowedSet = new HashSet<ConsoleContentAction>(allowed);
        var denied = new List<ConsoleContentAction>(candidates.Count);
        foreach (var action in candidates)
        {
            if (!allowedSet.Contains(action))
            {
                denied.Add(action);
            }
        }
        return denied;
    }
}
