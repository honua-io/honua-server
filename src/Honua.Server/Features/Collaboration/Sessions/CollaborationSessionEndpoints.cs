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
                // One canonical session/log key per draft regardless of the GUID textual form in
                // the route (honua-server#2999): sessions, appends, replay, and checkpoints agree.
                SavedMapCollaborationMapId.Normalize(mapId),
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

        // Session ids are NOT a secret capability: the participant id exposed to every session
        // member in snapshots equals the session id, so any participant could otherwise eject
        // any other (honua-server#2999 review). Scope the leave to the route's canonical map AND
        // to the caller's own identity (the same derivation join recorded); mismatches report
        // left=false exactly like an unknown session so probing cannot confirm one exists.
        var left = sessions.Leave(
            request.SessionId,
            reason: "left",
            requiredMapId: SavedMapCollaborationMapId.Normalize(mapId),
            requiredOwner: context.User);
        return Results.Json(
            ApiResponse<CollaborationLeaveResponse>.CreateSuccess(new CollaborationLeaveResponse { Left = left }),
            CollaborationSessionJsonContext.Default.ApiResponseCollaborationLeaveResponse);
    }
}
