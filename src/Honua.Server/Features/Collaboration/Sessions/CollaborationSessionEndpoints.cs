// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
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

        // Explicit session end for REST-joined sessions (#2999, REQ-001). WebSocket sessions end
        // on disconnect; the prune sweep reclaims anything that never says goodbye.
        group.MapPost("/leave", HandleLeave)
            .WithDisplayName("Leave Saved Map Collaboration Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<CollaborationLeaveResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // Authenticated WebSocket v1-envelope stream (#971/#2999). The join is authorized through
        // the Studio-lifecycle-backed authorizer before the upgrade, so an unauthorized client
        // receives a typed 401/403 rather than an opaque socket close.
        group.MapGet("/stream", CollaborationSessionStreamEndpoint.HandleStream)
            .WithDisplayName("Stream Saved Map Collaboration Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .ProducesProblem(StatusCodes.Status400BadRequest)
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

    private static IResult HandleLeave(
        string mapId,
        [FromBody] CollaborationLeaveRequest request,
        [FromServices] InMemoryCollaborationSessionService sessions,
        HttpContext context)
    {
        if (request.SessionId == Guid.Empty)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "sessionId is required.");
        }

        // Session ids are unguessable 128-bit handles returned only to the joining client, so
        // possession of the id is the leave capability; the map-level authorizer already gated
        // the join that produced it.
        var left = sessions.Leave(request.SessionId, reason: "left");
        return Results.Json(
            ApiResponse<CollaborationLeaveResponse>.CreateSuccess(new CollaborationLeaveResponse { Left = left }),
            CollaborationSessionJsonContext.Default.ApiResponseCollaborationLeaveResponse);
    }
}
