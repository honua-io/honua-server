// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Collaboration.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Collaboration.Operations;

/// <summary>
/// HTTP adapter over the saved-map collaborative edit operation log (#972): a durable op-log
/// append with monotonic server cursors plus idempotent dedupe, and a replay/catchup read from a
/// known cursor. Conflicts and out-of-window cursors surface as typed responses rather than silent
/// overwrites. Authorization reuses the shared saved-map collaboration authorizer (the
/// Studio-lifecycle-backed authorizer by default, #2999) so join and op-log writes share one
/// identity/RBAC seam. Accepted appends are echoed onto the live session stream as
/// server-ordered <c>operation-appended</c> envelopes.
/// </summary>
internal static class SavedMapOperationEndpoints
{
    internal sealed class SavedMapOperationLogCategory;

    public static void MapSavedMapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/saved-maps/{mapId}/collaboration/operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Saved Maps", "Collaboration")
            // Require an authenticated principal up front so unauthenticated requests fail at the
            // middleware boundary before reaching the per-map authorizer.
            .RequireAuthorization();

        group.MapPost("", HandleAppend)
            .WithDisplayName("Append Saved Map Collaboration Operation")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<SavedMapOperationAppendApiResponse>>()
            .Produces<ApiResponse<SavedMapOperationAppendApiResponse>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("", HandleReplay)
            .WithDisplayName("Replay Saved Map Collaboration Operations")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<ApiResponse<SavedMapOperationReplayApiResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> HandleAppend(
        string mapId,
        [FromBody] SavedMapOperationAppendApiRequest request,
        [FromServices] ISavedMapOperationLogRepository repository,
        [FromServices] ISavedMapCollaborationAuthorizer authorizer,
        [FromServices] InMemoryCollaborationSessionService sessions,
        [FromServices] ICollaborationSessionBackplane backplane,
        HttpContext context)
    {
        // One canonical log key per draft regardless of the GUID textual form in the route
        // (honua-server#2999): appends, replay, sessions, and checkpoints must all agree.
        mapId = SavedMapCollaborationMapId.Normalize(mapId);
        var authorizationError = await AuthorizeAsync(mapId, authorizer, context).ConfigureAwait(false);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        // Same fail-closed continuity rule as the checkpoint surface (honua-server#2999
        // review): with a distributed backplane but a process-local op log, two replicas would
        // each run an independent per-map cursor sequence — both could accept "cursor 1" and
        // broadcast conflicting committed operations that can never be reconciled. Reject the
        // edit honestly instead of accepting state the deployment cannot make authoritative.
        if (backplane.IsDistributed && !repository.SupportsReplicaSharedReplay)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Saved-map collaborative edits are unavailable in multi-replica deployments " +
                "until the collaboration operation log is backed by a replica-shared store; a " +
                "process-local log cannot assign authoritative operation cursors across replicas.");
        }

        if (!TryValidate(request, out var validationError) ||
            string.IsNullOrWhiteSpace(request.OperationId) ||
            request.Kind is null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                string.IsNullOrEmpty(validationError) ? "operationId and kind are required." : validationError);
        }

        if (request.BaseCursor < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "baseCursor must be zero or positive.");
        }

        var actorId = ResolveActorId(request.ActorId, context.User);
        if (actorId is null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "actorId is required when the principal has no stable identifier.");
        }

        var appendRequest = new SavedMapOperationAppendRequest
        {
            OperationId = new SavedMapOperationId(request.OperationId.Trim()),
            MapId = new SavedMapId(mapId),
            ActorId = new SavedMapActorId(actorId),
            BaseCursor = new SavedMapOperationCursor(request.BaseCursor),
            Kind = request.Kind.Value,
            Payload = request.Payload,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim()
        };

        var result = await repository.AppendAsync(appendRequest, context.RequestAborted).ConfigureAwait(false);

        if (result.Status == SavedMapOperationAppendStatus.Accepted && !result.IsDuplicate && result.Operation is not null)
        {
            // Bridge the committed op into the live session fan-out (#2999, REQ-002/REQ-004):
            // clients submit edits over this REST append (typed conflict semantics stay here) and
            // the WebSocket stream echoes the committed, server-ordered operation to every
            // participant of the map — the op-log server cursor is the authoritative order.
            sessions.PublishOperation(mapId, CollaborationOperationWire.FromEnvelope(result.Operation));
        }

        var response = new SavedMapOperationAppendApiResponse
        {
            Status = ToWireStatus(result.Status),
            Operation = result.Operation is null ? null : ToDto(result.Operation),
            HeadCursor = result.HeadCursor.Value,
            IsDuplicate = result.IsDuplicate,
            Conflict = result.Conflict is null ? null : ToDto(result.Conflict),
            Message = result.Message
        };

        return result.Status switch
        {
            SavedMapOperationAppendStatus.Accepted => Results.Json(
                ApiResponse<SavedMapOperationAppendApiResponse>.CreateSuccess(response),
                SavedMapOperationJsonContext.Default.ApiResponseSavedMapOperationAppendApiResponse),
            SavedMapOperationAppendStatus.Conflict => Results.Json(
                ApiResponse<SavedMapOperationAppendApiResponse>.Failure(
                    result.Message ?? "The operation conflicts with concurrent edits.", response),
                SavedMapOperationJsonContext.Default.ApiResponseSavedMapOperationAppendApiResponse,
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                ApiResponse<SavedMapOperationAppendApiResponse>.Failure(
                    result.Message ?? "The base cursor is outside the retained replay window.", response),
                SavedMapOperationJsonContext.Default.ApiResponseSavedMapOperationAppendApiResponse,
                statusCode: StatusCodes.Status409Conflict)
        };
    }

    private static async Task<IResult> HandleReplay(
        string mapId,
        [FromServices] ISavedMapOperationLogRepository repository,
        [FromServices] ISavedMapCollaborationAuthorizer authorizer,
        HttpContext context,
        [FromQuery] long? since = null)
    {
        mapId = SavedMapCollaborationMapId.Normalize(mapId);
        var authorizationError = await AuthorizeAsync(mapId, authorizer, context).ConfigureAwait(false);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        var sinceCursor = since.GetValueOrDefault(0);
        if (sinceCursor < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "since must be zero or positive.");
        }

        var result = await repository.ReplayAsync(
                new SavedMapId(mapId),
                new SavedMapOperationCursor(sinceCursor),
                context.RequestAborted)
            .ConfigureAwait(false);

        var response = new SavedMapOperationReplayApiResponse
        {
            Status = result.Status == SavedMapOperationReplayStatus.Ok ? "ok" : "resyncRequired",
            SinceCursor = result.SinceCursor.Value,
            HeadCursor = result.HeadCursor.Value,
            MinimumReplayCursor = result.MinimumReplayCursor.Value,
            Operations = result.Operations.Select(ToDto).ToArray(),
            Message = result.Message
        };

        return Results.Json(
            ApiResponse<SavedMapOperationReplayApiResponse>.CreateSuccess(response),
            SavedMapOperationJsonContext.Default.ApiResponseSavedMapOperationReplayApiResponse);
    }

    // Fail-closed per-map capability check shared with the session transport (#971/#972): a
    // generally authenticated principal must still be permitted on THIS saved map before its
    // edits enter the durable op-log. Reusing the join authorizer keeps presence and durable
    // edits on a single identity/RBAC seam. The middleware-level RequireAuthorization already
    // rejected anonymous callers, so this maps capability decisions to typed 401/403 results.
    private static async Task<IResult?> AuthorizeAsync(
        string mapId,
        ISavedMapCollaborationAuthorizer authorizer,
        HttpContext context)
    {
        var authorization = await authorizer
            .AuthorizeJoinAsync(mapId, context.User, context.RequestAborted)
            .ConfigureAwait(false);

        return authorization.Status switch
        {
            SavedMapCollaborationAuthorizationStatus.Authorized => null,
            SavedMapCollaborationAuthorizationStatus.RequiresAuthentication =>
                StandardErrorHelpers.CreateUnauthorized(
                    context,
                    authorization.Detail ?? "Authentication is required to edit this saved map."),
            _ => StandardErrorHelpers.CreateForbidden(
                context,
                authorization.Detail ?? "You are not allowed to edit this saved map.")
        };
    }

    private static string ToWireStatus(SavedMapOperationAppendStatus status) => status switch
    {
        SavedMapOperationAppendStatus.Accepted => "accepted",
        SavedMapOperationAppendStatus.Conflict => "conflict",
        _ => "resyncRequired"
    };

    private static SavedMapOperationEnvelopeDto ToDto(SavedMapOperationEnvelope operation) => new()
    {
        OperationId = operation.OperationId.Value,
        MapId = operation.MapId.Value,
        ActorId = operation.ActorId.Value,
        BaseCursor = operation.BaseCursor.Value,
        Kind = operation.Kind,
        ServerCursor = operation.ServerCursor.Value,
        AcceptedAt = operation.AcceptedAt,
        Payload = operation.Payload,
        IdempotencyKey = operation.IdempotencyKey
    };

    private static SavedMapOperationConflictDto ToDto(SavedMapOperationConflictResponse conflict) => new()
    {
        Code = conflict.Code,
        Message = conflict.Message,
        BaseCursor = conflict.BaseCursor.Value,
        HeadCursor = conflict.HeadCursor.Value,
        ConflictingOperations = conflict.ConflictingOperations.Select(ToDto).ToArray()
    };

    private static string? ResolveActorId(string? requestedActorId, ClaimsPrincipal principal)
    {
        if (!string.IsNullOrWhiteSpace(requestedActorId))
        {
            return requestedActorId.Trim();
        }

        var claimed = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.Name);
        return string.IsNullOrWhiteSpace(claimed) ? null : claimed.Trim();
    }

    private static bool TryValidate(SavedMapOperationAppendApiRequest request, out string error)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = string.Join(", ", results.Select(r => r.ErrorMessage));
        if (string.IsNullOrEmpty(error))
        {
            error = "Request validation failed.";
        }

        return false;
    }
}
