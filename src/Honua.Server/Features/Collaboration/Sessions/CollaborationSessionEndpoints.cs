// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationSessionEndpoints
{
    public static void MapCollaborationSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/saved-maps/{mapId}/collaboration/sessions")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Saved Maps", "Collaboration")
            // Mutation endpoints — require an authenticated principal up front so
            // unauthenticated requests are rejected at the middleware boundary,
            // before reaching the per-session authorizer in the handler.
            .RequireAuthorization();

        group.MapPost("/join", HandleJoin)
            .WithDisplayName("Join Saved Map Collaboration Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<CollaborationJoinResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> HandleJoin(
        string mapId,
        [FromBody] CollaborationJoinRequest request,
        [FromServices] InMemoryCollaborationSessionService sessions,
        HttpContext context)
    {
        var result = await sessions.JoinAsync(
                mapId,
                request,
                context.User,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (result.Response is not null)
        {
            return Results.Json(
                ApiResponse<CollaborationJoinResponse>.CreateSuccess(result.Response),
                CollaborationSessionJsonContext.Default.ApiResponseCollaborationJoinResponse);
        }

        return result.Authorization.Status switch
        {
            SavedMapCollaborationAuthorizationStatus.RequiresAuthentication =>
                StandardErrorHelpers.CreateUnauthorized(
                    context,
                    result.Authorization.Detail ?? "Authentication is required to join this collaboration session."),
            SavedMapCollaborationAuthorizationStatus.Forbidden =>
                StandardErrorHelpers.CreateForbidden(
                    context,
                    result.Authorization.Detail ?? "You are not allowed to join this collaboration session."),
            _ => StandardErrorHelpers.CreateForbidden(
                context,
                "You are not allowed to join this collaboration session.")
        };
    }
}
