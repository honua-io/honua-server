// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Outcome of an operator-selected disconnected-sync conflict resolution.
/// </summary>
/// <param name="Status">Terminal status of the resolution attempt.</param>
/// <param name="Record">The conflict record after a successful resolution.</param>
/// <param name="CommittedNewServerState">Whether the resolution produced a new committed server state.</param>
/// <param name="Effect">The feature-store effect the resolution applied.</param>
/// <param name="Message">Client-safe explanation for a non-success status.</param>
internal readonly record struct ReplicaConflictResolutionResult(
    ReplicaConflictResolutionStatus Status,
    ReplicaConflictRecord? Record,
    bool CommittedNewServerState,
    ReplicaConflictResolutionEffect Effect,
    string? Message);

/// <summary>
/// Terminal status of an operator-selected conflict resolution.
/// </summary>
internal enum ReplicaConflictResolutionStatus
{
    /// <summary>The resolution was applied and recorded.</summary>
    Applied = 0,

    /// <summary>The conflict does not exist (or does not belong to the addressed replica).</summary>
    NotFound = 1,

    /// <summary>The conflict was already resolved, including by a concurrent operator.</summary>
    AlreadyResolved = 2,

    /// <summary>The request payload was malformed for the selected action.</summary>
    InvalidRequest = 3,

    /// <summary>The action does not apply to this conflict's type or recorded state.</summary>
    NotApplicable = 4,

    /// <summary>
    /// The resolution needs a feature write, but this deployment has no replica-capable protocol
    /// adapter registered to perform it.
    /// </summary>
    WriteUnsupported = 5,

    /// <summary>The resolved feature state failed to commit through the shared edit pipeline.</summary>
    WriteFailed = 6,
}

/// <summary>
/// Applies operator-selected disconnected-sync conflict resolutions: plans the feature-store effect,
/// commits the resolved feature state through the shared edit pipeline, records the durable resolution
/// with the generation it actually produced, and emits audit and telemetry evidence (#2430).
/// </summary>
/// <remarks>
/// <para>
/// Conflict review used to be bookkeeping-only — resolving recorded an action and a generation cursor
/// but never wrote feature data, so under the default last-write-wins sync mode "keep server" left the
/// client's overwrite in place while reporting a new committed server state. This service closes that
/// gap: the write happens first through <see cref="IReplicaConflictResolutionApplier"/> (the shared
/// applyEdits pipeline, so entitlement, per-layer data-editor authorization, validation, geometry
/// conversion, telemetry, and the transactional outbox all apply), and the durable resolution is only
/// recorded once the write commits. A failed write leaves the conflict pending and reviewable rather
/// than marking it resolved against a state that never landed.
/// </para>
/// <para>
/// The service is deliberately protocol-neutral: it depends on the canonical applier abstraction, not
/// on any protocol adapter, so the admin conflict-review surface stays a thin adapter over shared
/// pipelines.
/// </para>
/// </remarks>
internal sealed partial class ReplicaConflictResolutionService
{
    private static readonly ActivitySource _activitySource = new("Honua.Server.ReplicaConflicts");

    private readonly IReplicaConflictRepository _conflictRepository;
    private readonly IChangeTracker _changeTracker;
    private readonly IAuditLog _auditLog;
    private readonly IReplicaConflictResolutionApplier? _applier;
    private readonly ILogger<ReplicaConflictResolutionService> _logger;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="conflictRepository">Durable conflict-record store.</param>
    /// <param name="changeTracker">Change-log reader used to stamp the produced server generation.</param>
    /// <param name="auditLog">Audit sink for resolution evidence.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="applier">
    /// Shared-edit-pipeline applier supplied by the replica-capable protocol adapter. Absent on
    /// deployments without one, in which case resolutions that need a write are reported as
    /// unsupported instead of being recorded against a state that was never written.
    /// </param>
    public ReplicaConflictResolutionService(
        IReplicaConflictRepository conflictRepository,
        IChangeTracker changeTracker,
        IAuditLog auditLog,
        ILogger<ReplicaConflictResolutionService> logger,
        IReplicaConflictResolutionApplier? applier = null)
    {
        _conflictRepository = conflictRepository ?? throw new ArgumentNullException(nameof(conflictRepository));
        _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applier = applier;
    }

    /// <summary>
    /// Resolves a pending conflict for a replica.
    /// </summary>
    /// <param name="request">The resolution request (conflict identity, action, operator inputs).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ReplicaConflictResolutionResult> ResolveAsync(
        ReplicaConflictResolutionServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("replicaconflict.resolve");
        activity?.SetTag("replica.id", request.ReplicaId);
        activity?.SetTag("replicaconflict.id", request.ConflictId);
        activity?.SetTag("replicaconflict.action", request.Action.ToString());

        var existing = await _conflictRepository.GetAsync(request.ConflictId, cancellationToken).ConfigureAwait(false);
        if (existing is not { } conflict ||
            !string.Equals(conflict.ReplicaId, request.ReplicaId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ReplicaConflictResolutionStatus.NotFound, message: null);
        }

        if (conflict.Status == ReplicaConflictStatus.Resolved)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        activity?.SetTag("replicaconflict.type", conflict.ConflictType.ToString());
        activity?.SetTag("replicaconflict.client_edit_applied", conflict.ClientEditApplied);

        var plan = ReplicaConflictResolutionPlanner.Plan(conflict, request.Action, request.Inputs);
        if (!plan.IsAccepted)
        {
            var status = plan.Rejection == ReplicaConflictResolutionRejection.InvalidRequest
                ? ReplicaConflictResolutionStatus.InvalidRequest
                : ReplicaConflictResolutionStatus.NotApplicable;
            activity?.SetStatus(ActivityStatusCode.Error, plan.RejectionMessage);
            return Failure(status, plan.RejectionMessage);
        }

        activity?.SetTag("replicaconflict.effect", plan.Effect.ToString());

        if (plan.Effect != ReplicaConflictResolutionEffect.None && _applier is null)
        {
            Log.ResolutionWriteUnsupported(_logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId);
            return Failure(
                ReplicaConflictResolutionStatus.WriteUnsupported,
                "Applying this resolution requires committing the resolved feature state, which this deployment cannot do: no replica-capable edit pipeline is registered.");
        }

        // Claim the conflict BEFORE writing feature state. The repository's guarded status transition
        // is the only single-winner primitive available, so it doubles as the claim: two operators
        // resolving the same conflict concurrently would otherwise both pass the pending check and both
        // dispatch an edit, letting the loser's write land last while the durable resolution and audit
        // evidence named the winner. The claim carries no resolved generation yet — that is stamped
        // below, once the write it describes has actually committed.
        var resolution = new ReplicaConflictResolution(
            request.ConflictId,
            request.Action,
            request.Actor,
            DateTimeOffset.UtcNow,
            ResolvedServerGeneration: null);

        var outcome = await _conflictRepository.ResolveAsync(resolution, cancellationToken).ConfigureAwait(false);
        if (outcome.Record is null)
        {
            return Failure(ReplicaConflictResolutionStatus.NotFound, message: null);
        }

        if (!outcome.Applied)
        {
            // A concurrent operator won the guarded claim; do not write, report success, or emit a
            // success audit event for this losing request.
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        var claimed = outcome.Record.Value;

        // Re-plan from the record the atomic claim returned, not the pre-claim read. Detection
        // post-processing for the originating sync can promote ClientEditApplied between the two, and
        // planning from the stale snapshot would then record a keep-server resolution as a no-op while
        // the client overwrite had in fact landed (#2430). The pre-claim plan above still runs first so
        // a malformed or inapplicable request is rejected before anything is claimed.
        plan = ReplicaConflictResolutionPlanner.Plan(claimed, request.Action, request.Inputs);
        if (!plan.IsAccepted)
        {
            await ReleaseClaimAsync(conflict, CancellationToken.None).ConfigureAwait(false);
            var replanStatus = plan.Rejection == ReplicaConflictResolutionRejection.InvalidRequest
                ? ReplicaConflictResolutionStatus.InvalidRequest
                : ReplicaConflictResolutionStatus.NotApplicable;
            activity?.SetStatus(ActivityStatusCode.Error, plan.RejectionMessage);
            return Failure(replanStatus, plan.RejectionMessage);
        }

        if (plan.Effect != ReplicaConflictResolutionEffect.None && _applier is null)
        {
            await ReleaseClaimAsync(conflict, CancellationToken.None).ConfigureAwait(false);
            Log.ResolutionWriteUnsupported(_logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId);
            return Failure(
                ReplicaConflictResolutionStatus.WriteUnsupported,
                "Applying this resolution requires committing the resolved feature state, which this deployment cannot do: no replica-capable edit pipeline is registered.");
        }

        activity?.SetTag("replicaconflict.effect", plan.Effect.ToString());

        if (plan.Effect != ReplicaConflictResolutionEffect.None)
        {
            ReplicaConflictApplyResult applyResult;
            try
            {
                applyResult = await _applier!.ApplyAsync(
                    new ReplicaConflictResolutionCommand(
                        conflict.ServiceId,
                        conflict.LayerId,
                        conflict.ObjectId,
                        plan.Effect,
                        plan.FeatureStateJson),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The claim has already moved the record out of the reviewable state, so an aborted
                // apply — a cancelled request, a transport fault, a provider exception — must not
                // leave the conflict permanently reported as resolved. Release with a fresh token:
                // the request's own token is typically the thing that was cancelled.
                await ReleaseClaimAsync(conflict, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionWriteFailed(
                    _logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId, conflict.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            if (!applyResult.Applied)
            {
                // Release the claim so the conflict stays reviewable: recording a resolution for a
                // state that never committed is exactly the dishonesty this path exists to remove.
                // The cleanup uses a fresh token, not the request's: a write can fail precisely
                // because the request was cancelled, and releasing on the cancelled token would throw
                // out of the cleanup and leave the conflict claimed with nothing written.
                await ReleaseClaimAsync(conflict, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionWriteFailed(
                    _logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId, conflict.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, applyResult.FailureMessage);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteFailed,
                    applyResult.FailureMessage ?? "The resolved conflict state could not be committed.");
            }
        }

        // Past this point the feature write has committed, so finalization must not be abandoned
        // half-done: a cancelled request token here would throw with the conflict already transitioned
        // to Resolved, leaving the produced generation unpersisted, the audit event unwritten, and
        // retries answered with AlreadyResolved. Finalization therefore runs on an uncancellable token.
        var finalizationToken = CancellationToken.None;

        // The generation cursor is read AFTER the write so it names the generation the resolution
        // actually produced, not the one that happened to be current when the request arrived.
        if (plan.CommittedNewServerState)
        {
            var resolvedGeneration = await _changeTracker.GetCurrentGenerationAsync(finalizationToken)
                .ConfigureAwait(false);
            resolution = resolution with { ResolvedServerGeneration = resolvedGeneration };
            claimed = claimed with { ResolvedServerGeneration = resolvedGeneration };
            await _conflictRepository.UpsertAsync(claimed, finalizationToken).ConfigureAwait(false);
        }

        await RecordAuditAsync(request, conflict, plan, resolution, finalizationToken).ConfigureAwait(false);

        Log.ResolutionApplied(
            _logger,
            conflict.ConflictId,
            conflict.ServiceId,
            conflict.LayerId,
            conflict.ObjectId,
            request.Action,
            plan.Effect);

        return new ReplicaConflictResolutionResult(
            ReplicaConflictResolutionStatus.Applied,
            claimed,
            plan.CommittedNewServerState,
            plan.Effect,
            Message: null);
    }

    /// <summary>
    /// Returns a claimed conflict to the pending, reviewable state after its feature write failed to
    /// commit, so the failed attempt does not leave a terminal resolution recorded against a state
    /// that never landed. Always invoked with a fresh cancellation token by its callers, because the
    /// write it is cleaning up after may itself have failed due to cancellation. Any failure to
    /// release is logged rather than thrown — including cancellation — since the caller is already
    /// reporting the write failure and masking that with a second error would lose the real cause.
    /// </summary>
    private async Task ReleaseClaimAsync(ReplicaConflictRecord conflict, CancellationToken cancellationToken)
    {
        try
        {
            await _conflictRepository.UpsertAsync(
                conflict with
                {
                    Status = ReplicaConflictStatus.Pending,
                    ResolutionAction = null,
                    ResolvedBy = null,
                    ResolvedAt = null,
                    ResolvedServerGeneration = null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ResolutionClaimReleaseFailed(_logger, conflict.ConflictId, ex);
        }
    }

    private async Task RecordAuditAsync(
        ReplicaConflictResolutionServiceRequest request,
        ReplicaConflictRecord conflict,
        ReplicaConflictResolutionPlan plan,
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(
            new ReplicaConflictAuditDetails
            {
                Action = request.ActionName,
                Effect = plan.Effect.ToString(),
                CommittedNewServerState = plan.CommittedNewServerState,
                ServiceId = conflict.ServiceId,
                LayerId = conflict.LayerId,
                ObjectId = conflict.ObjectId,
                ResolvedServerGeneration = resolution.ResolvedServerGeneration,
            },
            ReplicaManagementJsonContext.Default.ReplicaConflictAuditDetails);

        await _auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = resolution.ResolvedAt,
                EventType = AuditEventType.AdminAction,
                Actor = request.Actor,
                ActorType = AuditActorType.UserId,
                ResourceType = "replica_conflict",
                ResourceId = request.ConflictId,
                Action = $"replica.conflict.resolve.{request.ActionName}",
                Outcome = AuditOutcome.Success,
                CorrelationId = request.CorrelationId,
                Details = details,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ReplicaConflictResolutionResult Failure(ReplicaConflictResolutionStatus status, string? message)
        => new(status, Record: null, CommittedNewServerState: false, ReplicaConflictResolutionEffect.None, message);

    private static partial class Log
    {
        [LoggerMessage(EventId = 7745, Level = LogLevel.Information,
            Message = "Resolved replica conflict {ConflictId} for service {ServiceId} layer {LayerId} objectId {ObjectId} with action {Action} (effect {Effect})")]
        public static partial void ResolutionApplied(
            ILogger logger,
            string conflictId,
            string serviceId,
            int layerId,
            long objectId,
            ReplicaConflictResolutionAction action,
            ReplicaConflictResolutionEffect effect);

        [LoggerMessage(EventId = 7746, Level = LogLevel.Warning,
            Message = "Replica conflict {ConflictId} for service {ServiceId} layer {LayerId} objectId {ObjectId} was left pending: the resolved feature state failed to commit")]
        public static partial void ResolutionWriteFailed(
            ILogger logger, string conflictId, string serviceId, int layerId, long objectId);

        [LoggerMessage(EventId = 7747, Level = LogLevel.Warning,
            Message = "Replica conflict {ConflictId} for service {ServiceId} layer {LayerId} needs a feature write but no replica conflict-resolution applier is registered")]
        public static partial void ResolutionWriteUnsupported(
            ILogger logger, string conflictId, string serviceId, int layerId);

        [LoggerMessage(EventId = 7748, Level = LogLevel.Error,
            Message = "Replica conflict {ConflictId} could not be returned to the pending state after its resolved feature state failed to commit; it may remain recorded as resolved")]
        public static partial void ResolutionClaimReleaseFailed(ILogger logger, string conflictId, Exception exception);
    }
}

/// <summary>
/// Request handed to <see cref="ReplicaConflictResolutionService"/> by the admin conflict-review
/// endpoint.
/// </summary>
/// <param name="ReplicaId">Replica the conflict must belong to.</param>
/// <param name="ConflictId">Conflict being resolved.</param>
/// <param name="Action">Operator-selected resolution action.</param>
/// <param name="ActionName">Wire spelling of the action, used for audit evidence.</param>
/// <param name="Inputs">Operator inputs for merge/geometry actions.</param>
/// <param name="Actor">Resolving operator (audit evidence).</param>
/// <param name="CorrelationId">Request correlation id.</param>
internal readonly record struct ReplicaConflictResolutionServiceRequest(
    string ReplicaId,
    string ConflictId,
    ReplicaConflictResolutionAction Action,
    string ActionName,
    ReplicaConflictResolutionInputs Inputs,
    string Actor,
    string CorrelationId);
