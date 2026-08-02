// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
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
    /// <summary>
    /// Maximum length of a checkpoint change note, matching the
    /// <c>[StringLength(1000)]</c> the canonical <c>SaveStudioContentVersionRequest.ChangeNote</c>
    /// declares. Both write the field into the same immutable version storage, so the two
    /// admission paths must agree.
    /// </summary>
    internal const int MaxChangeNoteLength = 1000;

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

        // A checkpoint writes the note into the SAME immutable version storage the canonical
        // save path writes to, and that path caps the field at
        // SaveStudioContentVersionRequest.ChangeNote's [StringLength(1000)]. Enforcing the
        // identical bound here keeps this from being an alternate admission route that places
        // request-sized notes in immutable storage (honua-server#2999 review). Checked before
        // any draft read or mutation so an oversized note costs nothing.
        if (request.ChangeNote is { Length: > MaxChangeNoteLength })
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"changeNote must be {MaxChangeNoteLength} characters or fewer.");
        }

        if (request.SupersedeConflictingOperationCursor is <= 0)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "supersedeConflictingOperationCursor must be a positive server cursor.");
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

        // Same rule one axis over: a checkpoint mints an IMMUTABLE version, so it must be able to
        // prove it observed every accepted edit. A process-local log loses acknowledged
        // operations on restart and a post-restart replay is indistinguishable from a session
        // that never appended anything — the checkpoint would return 201 for a version that
        // silently drops those edits. Refuse honestly instead of faking durability
        // (honua-server#2999 review). Live co-editing itself stays available: appends and the
        // stream are explicitly resumable and tell a reconnecting client to resync.
        if (!operationLog.CanProveRestartContinuity)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Saved-map checkpoints are unavailable until the collaboration operation log is " +
                "backed by a restart-durable store; a process-local log loses accepted " +
                "operations when the server restarts, so a checkpoint cannot prove the version " +
                "it would create contains every accepted edit.");
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

        // Replay everything after the last successfully checkpointed cursor (0 when none is
        // recorded). The replay start is fully server-derived — a client-supplied cursor is
        // never trusted, because an arbitrary client value could silently drop accepted
        // operations from the immutable version. Replaying an already-applied prefix is
        // idempotent: all supported operation families are absolute-state. A ResyncRequired
        // result means never-checkpointed operations have been pruned out of the retained
        // window, so the checkpoint MUST fail closed: pruned operations are not necessarily
        // superseded by the retained tail (an old change to one field followed by many changes
        // to another field would be silently dropped).
        var replay = await operationLog.ReplayPendingAsync(canonicalMapId, context.RequestAborted)
            .ConfigureAwait(false);
        if (replay.Status == SavedMapOperationReplayStatus.ResyncRequired)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                "Operations that were never checkpointed have been pruned out of the retained " +
                "replay window; the checkpoint cannot prove it captures every accepted edit. " +
                "Checkpoint more frequently or restart the session from the latest saved version.");
        }

        var actorId = ConsolePrincipal.ResolveActorId(context.User);
        try
        {
            // Generation that actually received the replayed operations. Null when nothing was
            // replayed: there is then no applied edit a concurrent write could displace, so a
            // no-op checkpoint of the current draft state stays valid.
            long? appliedGeneration = null;
            SavedMapCheckpointSupersededOperation? supersededOperation = null;
            var appliedOperationCount = replay.Operations.Count;
            if (replay.Operations.Count > 0 || request.SupersedeConflictingOperationCursor.HasValue)
            {
                StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(draft.Family);
                var applyResult = SavedMapOperationDraftApplier.ApplyForCheckpoint(
                    draft.Envelope,
                    replay.Operations,
                    request.SupersedeConflictingOperationCursor);
                supersededOperation = applyResult.SupersededOperation;
                appliedOperationCount -= supersededOperation is null ? 0 : 1;

                if (appliedOperationCount > 0)
                {
                    var updated = await lifecycle.UpdateDraftAsync(
                            draftId,
                            new UpdateStudioPackageDraftCommand
                            {
                                PackageKey = draft.PackageKey,
                                WorkspaceId = draft.WorkspaceId,
                                OwnerId = draft.OwnerId,
                                Envelope = applyResult.Envelope,
                                Generation = draft.Generation,
                                ActorId = actorId,
                            },
                            context.RequestAborted)
                        .ConfigureAwait(false);
                    if (updated is null)
                    {
                        return StandardErrorHelpers.CreateNotFound(context, "Studio package draft was not found.");
                    }

                    appliedGeneration = updated.Generation;
                }
                else if (supersededOperation is not null)
                {
                    // Even when reconciliation supersedes the only pending operation, version
                    // exactly the generation against which the missing-target conflict was
                    // proven. A concurrent draft update could reintroduce the target and make the
                    // acknowledged supersession stale; the lifecycle generation guard must then
                    // reject this checkpoint without advancing its cursor.
                    appliedGeneration = draft.Generation;
                }
            }

            // Version EXACTLY the generation that received the replay. SaveDraftAsVersionAsync
            // re-reads the draft, so another Studio draft update landing in between would
            // otherwise be versioned instead — and because MarkCheckpointed still advanced the
            // cursor below, the replayed operations would be permanently skipped by every later
            // checkpoint. Failing closed here leaves the cursor unadvanced, so a retry replays the
            // same operations onto the current draft (honua-server#2999 review).
            var version = await lifecycle.SaveDraftAsVersionAsync(
                    draftId,
                    BuildChangeNote(request.ChangeNote, supersededOperation),
                    actorId,
                    appliedGeneration,
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (version is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Studio package draft was not found.");
            }

            // Only after the immutable version is durably saved: later checkpoints replay just
            // the suffix after this head, so a long session stays checkpointable after the
            // already-persisted prefix ages out of the retained window.
            operationLog.MarkCheckpointed(canonicalMapId, replay.HeadCursor.Value);

            var response = new CollaborationCheckpointResponse
            {
                MapId = canonicalMapId,
                ItemId = version.ItemId,
                VersionId = version.VersionId,
                VersionNumber = version.VersionNumber,
                ContentHash = version.ContentHash,
                CreatedAt = version.CreatedAt,
                AppliedOperationCount = appliedOperationCount,
                HeadCursor = replay.HeadCursor.Value,
                SupersededOperations = supersededOperation is null
                    ? []
                    :
                    [
                        new CollaborationCheckpointSupersededOperationResponse
                        {
                            OperationId = supersededOperation.Operation.OperationId.Value,
                            Kind = supersededOperation.Operation.Kind,
                            ServerCursor = supersededOperation.Operation.ServerCursor.Value,
                            Reason = supersededOperation.Reason,
                        },
                    ],
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
        catch (SavedMapCheckpointStateConflictException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Saved-map checkpoint requires reconciliation",
                type: "urn:honua:problem:collaboration-checkpoint-reconciliation-required",
                extensions: new Dictionary<string, object?>
                {
                    ["conflictCode"] = "missing-composition-target",
                    ["operationId"] = ex.Operation.OperationId.Value,
                    ["operationKind"] = ex.Operation.Kind.ToString(),
                    ["operationCursor"] = ex.Operation.ServerCursor.Value,
                    ["resolutionField"] = "supersedeConflictingOperationCursor",
                });
        }
        catch (SavedMapCheckpointReconciliationException ex)
        {
            return StandardErrorHelpers.CreateConflict(context, ex.Message);
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

    private static string BuildChangeNote(
        string? requestedChangeNote,
        SavedMapCheckpointSupersededOperation? supersededOperation)
    {
        var baseNote = string.IsNullOrWhiteSpace(requestedChangeNote)
            ? "Live collaboration checkpoint"
            : requestedChangeNote.Trim();
        if (supersededOperation is null)
        {
            return baseNote;
        }

        var operation = supersededOperation.Operation;
        var auditNote =
            $"Reconciled collaboration checkpoint: superseded operation '{operation.OperationId.Value}' " +
            $"at cursor {operation.ServerCursor.Value} ({operation.Kind}).";
        var maximumBaseNoteLength = MaxChangeNoteLength - auditNote.Length - 1;
        if (maximumBaseNoteLength <= 0)
        {
            return auditNote.Length <= MaxChangeNoteLength
                ? auditNote
                : auditNote[..MaxChangeNoteLength];
        }

        if (baseNote.Length > maximumBaseNoteLength)
        {
            baseNote = baseNote[..maximumBaseNoteLength].TrimEnd();
        }

        return string.IsNullOrEmpty(baseNote) ? auditNote : $"{baseNote} {auditNote}";
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
/// full retained log): a client-supplied replay-start cursor is deliberately NOT accepted, because
/// trusting one could silently drop accepted operations from the immutable version. The optional
/// reconciliation cursor does not change the window; it can acknowledge only a typed conflict the
/// server reproduces at that exact operation (honua-server#2999, honua-server#3047).
/// </summary>
internal sealed record CollaborationCheckpointRequest
{
    /// <summary>
    /// Optional change note recorded on the immutable version. Bounded by the same
    /// 1000-character limit the canonical
    /// <see cref="Honua.Server.Features.Studio.Models.SaveStudioContentVersionRequest.ChangeNote"/>
    /// declares, since both end up in the same immutable version storage. The attribute
    /// documents the contract for the generated OpenAPI; the handler enforces it, because
    /// minimal APIs do not run data annotations on a bound body by themselves.
    /// </summary>
    [StringLength(CollaborationCheckpointEndpoints.MaxChangeNoteLength)]
    public string? ChangeNote { get; init; }

    /// <summary>
    /// Exact server cursor from a typed checkpoint conflict response that the authorized owner
    /// explicitly chooses to supersede. The cursor is skipped only when replay proves that same
    /// operation still targets missing composition state; a valid operation cannot be discarded.
    /// </summary>
    public long? SupersedeConflictingOperationCursor { get; init; }
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

    /// <summary>
    /// Accepted operations the owner explicitly superseded while resolving this checkpoint.
    /// Empty for ordinary checkpoints.
    /// </summary>
    public IReadOnlyList<CollaborationCheckpointSupersededOperationResponse> SupersededOperations { get; init; } = [];
}

/// <summary>Wire record of one explicitly superseded collaboration operation.</summary>
internal sealed record CollaborationCheckpointSupersededOperationResponse
{
    /// <summary>Client-generated identity of the superseded operation.</summary>
    public required string OperationId { get; init; }

    /// <summary>Operation family that could not be replayed.</summary>
    public required SavedMapOperationKind Kind { get; init; }

    /// <summary>Server cursor the owner acknowledged.</summary>
    public required long ServerCursor { get; init; }

    /// <summary>State conflict that required reconciliation.</summary>
    public required string Reason { get; init; }
}

/// <summary>Source-generated JSON context for the collaboration checkpoint surface.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CollaborationCheckpointRequest))]
[JsonSerializable(typeof(CollaborationCheckpointResponse))]
[JsonSerializable(typeof(CollaborationCheckpointSupersededOperationResponse))]
[JsonSerializable(typeof(ApiResponse<CollaborationCheckpointResponse>))]
internal sealed partial class CollaborationCheckpointJsonContext : JsonSerializerContext
{
}
