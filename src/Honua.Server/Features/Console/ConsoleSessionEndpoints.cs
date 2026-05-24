// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Console session bootstrap endpoint.
/// </summary>
internal static class ConsoleSessionEndpoints
{
    /// <summary>
    /// Log category for the Console session endpoint.
    /// </summary>
    internal sealed class ConsoleSessionEndpointLog;

    /// <summary>
    /// Maps the Console session bootstrap endpoint into the supplied builder.
    /// </summary>
    public static void MapConsoleSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/session", HandleGetSession)
            .WithDisplayName("Get Console Session Bootstrap")
            .WithSummary("Returns the current user profile, capabilities, navigation entitlements, and an initial page of content items so Console can initialize without sequential round trips.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleSessionContext>>, ProblemHttpResult>> HandleGetSession(
        [FromServices] IConsoleContentStore store,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleSessionEndpointLog> logger,
        HttpContext context,
        [FromQuery] int limit = 25)
    {
        try
        {
            var principal = context.User;
            var capabilities = await evaluator.ResolveCapabilitiesAsync(principal, context.RequestAborted).ConfigureAwait(false);
            var entitlements = await evaluator.EvaluateRouteEntitlementsAsync(principal, ConsoleActionEvaluator.DefaultRouteKeys, context.RequestAborted).ConfigureAwait(false);

            var query = new ConsoleContentQuery { Limit = limit <= 0 ? 25 : limit };
            ConsoleContentListResult listResult;
            var degraded = false;
            try
            {
                listResult = await store.ListAsync(query, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception listEx) when (listEx is not OperationCanceledException)
            {
                ConsoleEndpointsLog.EndpointFailed(logger, "session.content-page", listEx);
                listResult = new ConsoleContentListResult { Items = Array.Empty<ConsoleContentItem>(), Total = 0 };
                degraded = true;
            }

            var items = await ConsoleContentMapper.WithComputedActionsAsync(listResult.Items, evaluator, principal, context.RequestAborted).ConfigureAwait(false);

            var response = new ConsoleSessionContext
            {
                User = BuildUserProfile(principal),
                Capabilities = capabilities,
                NavigationEntitlements = entitlements,
                Content = new ConsoleContentPage
                {
                    Items = items,
                    Total = listResult.Total,
                    NextCursor = listResult.NextCursor,
                },
                Degraded = degraded,
            };

            ConsoleEndpointsLog.SessionLoaded(logger, response.User.Id, items.Count, entitlements.Count);
            return TypedResults.Ok(ApiResponse<ConsoleSessionContext>.CreateSuccess(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "session.load", ex);
            return TypedResults.Problem(
                title: "Console session load failed",
                detail: "An internal error occurred while assembling the Console session.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static ConsoleUserProfile BuildUserProfile(ClaimsPrincipal principal)
    {
        // Use the shared resolver so admin API-key principals (no
        // NameIdentifier/sub claim) report a non-empty user id instead of
        // an empty string.
        var userId = ConsolePrincipal.ResolveActorId(principal) ?? string.Empty;
        var name = principal.Identity?.Name ?? principal.FindFirstValue("name");
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        var avatarUrl = principal.FindFirstValue("picture") ?? principal.FindFirstValue("avatar_url");
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return new ConsoleUserProfile
        {
            Id = userId,
            Name = name,
            Email = email,
            AvatarUrl = avatarUrl,
            Roles = roles,
        };
    }
}
