// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Console;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Persistence bridge from a live collaboration session to the Studio package lifecycle
/// (honua-server#2999, REQ-003/AC-2): checkpoints replay the retained saved-map operation log
/// onto the target Studio draft's composition body and save the result as an immutable
/// <c>StudioContentVersion</c> via the canonical lifecycle service. Callable at any time during a
/// session — session end simply calls the same endpoint. Authorization mirrors the Studio
/// version-save path: <c>CreateVersion</c> against both the draft owner and the parent content
/// item owner, audited through the shared <see cref="StudioEndpointAuthorization"/> seam.
/// </summary>
internal static class CollaborationCheckpointEndpoints
{
    public static void MapCollaborationCheckpointEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/saved-maps/{mapId}/collaboration/checkpoints")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Saved Maps", "Collaboration")
            .RequireAuthorization();

        group.MapPost("", HandleCheckpoint)
            .WithDisplayName("Checkpoint Saved Map Collaboration Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<CollaborationCheckpointResponse>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleCheckpoint(
        string mapId,
        [FromBody] CollaborationCheckpointRequest request,
        [FromServices] IStudioPackageLifecycleService lifecycle,
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] SavedMapCheckpointOperationLog operationLog,
        [FromServices] ILogger<CollaborationCheckpointEndpointsMarker> logger,
        HttpContext context)
    {
        if (!Guid.TryParse(mapId, out var draftId))
        {
            return StandardErrorHelpers.CreateNotFound(
                context, "The map id does not resolve to a Studio package draft.");
        }

        // Sessions, appends, replay, and checkpoints must share ONE log key per draft, so the
        // route value (which authorization accepts in any GUID textual form) is canonicalized
        // to the "D" form before touching the op log.
        var canonicalMapId = SavedMapCollaborationMapId.Normalize(mapId);

        // A distributed deployment routes an append and its checkpoint to arbitrary nodes, but
        // a process-local op log only replays operations accepted by THIS node — a replay here
        // could silently omit accepted edits and mint an incomplete immutable version. Fail
        // closed with an honest error until the op log is backed by a replica-shared store
        // (honua-server#2999); do not fake durability.
        if (!operationLog.CanProveReplayContinuity)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Saved-map checkpoints are unavailable in multi-replica deployments until the " +
                "collaboration operation log is backed by a replica-shared store; the " +
                "process-local log cannot prove it observed every accepted edit.");
        }

        var draft = await lifecycle.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
        if (draft is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context, "The map id does not resolve to a Studio package draft.");
        }

        // Mirror HandleCreateContentVersion's two-boundary authorization: saving also advances
        // the parent content item's current pointer, so draft ownership alone must not authorize
        // the checkpoint.
        var draftDecision = await authorization.AuthorizeAsync(
            context,
            StudioAuthorizationOperation.CreateVersion,
            draft.OwnerId,
            resourceType: "studio-package-draft",
            resourceId: draftId.ToString("D")).ConfigureAwait(false);
        if (!draftDecision.IsAllowed)
        {
            return StandardErrorHelpers.CreateForbidden(
                context, draftDecision.Reason ?? "You are not allowed to checkpoint this saved map.");
        }

        var pointers = await lifecycle.GetPointersAsync(draft.ItemId, context.RequestAborted).ConfigureAwait(false);
        if (pointers is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Studio content item was not found.");
        }

        var itemDecision = await authorization.AuthorizeAsync(
            context,
            StudioAuthorizationOperation.CreateVersion,
            pointers.OwnerId,
            resourceType: "studio-content-item",
            resourceId: draft.ItemId.ToString("D")).ConfigureAwait(false);
        if (!itemDecision.IsAllowed)
        {
            return StandardErrorHelpers.CreateForbidden(
                context, itemDecision.Reason ?? "You are not allowed to checkpoint this saved map.");
        }

        // Replay the FULL retained log from the earliest provable cursor (0). The replay start
        // is server-derived — a client-supplied cursor is never trusted, because the server does
        // not track which cursor was last applied to the draft and an arbitrary client value
        // could silently drop accepted operations from the immutable version. Replaying an
        // already-applied prefix is idempotent: all supported operation families are
        // absolute-state. When the start of the log has been pruned out of the retained window
        // the checkpoint MUST fail closed: pruned operations are not necessarily superseded by
        // the retained tail (an old change to one field followed by many changes to another
        // field would be silently dropped), so refuse to persist an unprovable state.
        var replay = await operationLog.ReplayAllAsync(canonicalMapId, context.RequestAborted)
            .ConfigureAwait(false);
        if (replay.Status == SavedMapOperationReplayStatus.ResyncRequired)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                "The start of the operation log has been pruned out of the retained replay " +
                "window; the checkpoint cannot prove it captures every accepted edit. " +
                "Checkpoint more frequently or restart the session from the latest saved version.");
        }

        var actorId = ConsolePrincipal.ResolveActorId(context.User);
        try
        {
            if (replay.Operations.Count > 0)
            {
                StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(draft.Family);
                var envelope = SavedMapOperationDraftApplier.Apply(draft.Envelope, replay.Operations);
                var updated = await lifecycle.UpdateDraftAsync(
                        draftId,
                        new UpdateStudioPackageDraftCommand
                        {
                            PackageKey = draft.PackageKey,
                            WorkspaceId = draft.WorkspaceId,
                            OwnerId = draft.OwnerId,
                            Envelope = envelope,
                            Generation = draft.Generation,
                            ActorId = actorId,
                        },
                        context.RequestAborted)
                    .ConfigureAwait(false);
                if (updated is null)
                {
                    return StandardErrorHelpers.CreateNotFound(context, "Studio package draft was not found.");
                }
            }

            var version = await lifecycle.SaveDraftAsVersionAsync(
                    draftId,
                    string.IsNullOrWhiteSpace(request.ChangeNote) ? "Live collaboration checkpoint" : request.ChangeNote.Trim(),
                    actorId,
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (version is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Studio package draft was not found.");
            }

            var response = new CollaborationCheckpointResponse
            {
                MapId = canonicalMapId,
                ItemId = version.ItemId,
                VersionId = version.VersionId,
                VersionNumber = version.VersionNumber,
                ContentHash = version.ContentHash,
                CreatedAt = version.CreatedAt,
                AppliedOperationCount = replay.Operations.Count,
                HeadCursor = replay.HeadCursor.Value,
            };
            return Results.Json(
                ApiResponse<CollaborationCheckpointResponse>.CreateSuccess(response),
                CollaborationCheckpointJsonContext.Default.ApiResponseCollaborationCheckpointResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (SavedMapCheckpointUnsupportedOperationException ex)
        {
            // Fail closed: an op-log entry the applier cannot express on the composition body
            // must not be silently dropped from the durable version.
            return StandardErrorHelpers.CreateUnprocessableEntity(context, ex.Message);
        }
        catch (SavedMapCheckpointPayloadException ex)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(context, ex.Message);
        }
        catch (StudioCompositionNotFoundException ex)
        {
            return StandardErrorHelpers.CreateConflict(context, ex.Message);
        }
        catch (StudioCompositionFamilyException ex)
        {
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (StudioCompositionBodyException ex)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Draft generation conflict or family/lifecycle invariant surfaced by the canonical
            // lifecycle service. Log the original exception and return a stable message so
            // provider/implementation details never leak to the client.
            CollaborationCheckpointLog.CheckpointConflict(logger, mapId, ex);
            return StandardErrorHelpers.CreateConflict(
                context,
                "The draft changed concurrently or the Studio lifecycle rejected the checkpoint. " +
                "Retry the checkpoint against the current draft state.");
        }
    }
}

/// <summary>Logger category marker for the collaboration checkpoint endpoints.</summary>
internal sealed class CollaborationCheckpointEndpointsMarker;

/// <summary>Source-generated logging for the collaboration checkpoint surface.</summary>
internal static partial class CollaborationCheckpointLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Saved-map collaboration checkpoint conflict for map {MapId}.")]
    public static partial void CheckpointConflict(ILogger logger, string mapId, Exception exception);
}

/// <summary>
/// Request body for a live-session checkpoint. The replay window is server-derived (always the
/// full retained log): a client-supplied cursor is deliberately NOT accepted, because the server
/// does not track the draft's last-applied cursor and trusting an arbitrary client value could
/// silently drop accepted operations from the immutable version (honua-server#2999).
/// </summary>
internal sealed record CollaborationCheckpointRequest
{
    /// <summary>Optional change note recorded on the immutable version.</summary>
    public string? ChangeNote { get; init; }
}

/// <summary>
/// Response for a live-session checkpoint: the immutable Studio content version produced plus the
/// op-log window that was applied.
/// </summary>
internal sealed record CollaborationCheckpointResponse
{
    /// <summary>Saved map (Studio draft) that was checkpointed.</summary>
    public required string MapId { get; init; }

    /// <summary>Parent Studio content item.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Immutable content version produced by the checkpoint.</summary>
    public required Guid VersionId { get; init; }

    /// <summary>Monotonic version number within the item.</summary>
    public required int VersionNumber { get; init; }

    /// <summary>Content hash of the immutable envelope.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Version creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Number of op-log operations applied to the draft before saving.</summary>
    public required int AppliedOperationCount { get; init; }

    /// <summary>Op-log head cursor captured by this checkpoint.</summary>
    public required long HeadCursor { get; init; }
}

/// <summary>Source-generated JSON context for the collaboration checkpoint surface.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CollaborationCheckpointRequest))]
[JsonSerializable(typeof(CollaborationCheckpointResponse))]
[JsonSerializable(typeof(ApiResponse<CollaborationCheckpointResponse>))]
internal sealed partial class CollaborationCheckpointJsonContext : JsonSerializerContext
{
}
