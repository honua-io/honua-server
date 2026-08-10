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

        if (plan.Effect != ReplicaConflictResolutionEffect.None)
        {
            if (_applier is null)
            {
                Log.ResolutionWriteUnsupported(_logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteUnsupported,
                    "Applying this resolution requires committing the resolved feature state, which this deployment cannot do: no replica-capable edit pipeline is registered.");
            }

            var applyResult = await _applier.ApplyAsync(
                new ReplicaConflictResolutionCommand(
                    conflict.ServiceId,
                    conflict.LayerId,
                    conflict.ObjectId,
                    plan.Effect,
                    plan.FeatureStateJson),
                cancellationToken).ConfigureAwait(false);

            if (!applyResult.Applied)
            {
                // Leave the conflict pending: recording a resolution for a state that never committed
                // is exactly the dishonesty this path exists to remove.
                Log.ResolutionWriteFailed(
                    _logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId, conflict.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, applyResult.FailureMessage);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteFailed,
                    applyResult.FailureMessage ?? "The resolved conflict state could not be committed.");
            }
        }

        // The generation cursor is read AFTER the write so it names the generation the resolution
        // actually produced, not the one that happened to be current when the request arrived.
        long? resolvedGeneration = plan.CommittedNewServerState
            ? await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var resolution = new ReplicaConflictResolution(
            request.ConflictId,
            request.Action,
            request.Actor,
            DateTimeOffset.UtcNow,
            resolvedGeneration);

        var outcome = await _conflictRepository.ResolveAsync(resolution, cancellationToken).ConfigureAwait(false);
        if (outcome.Record is null)
        {
            return Failure(ReplicaConflictResolutionStatus.NotFound, message: null);
        }

        if (!outcome.Applied)
        {
            // A concurrent operator won the guarded update; do not report this losing request as a
            // success or emit a success audit event.
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        await RecordAuditAsync(request, conflict, plan, resolution, cancellationToken).ConfigureAwait(false);

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
            outcome.Record,
            plan.CommittedNewServerState,
            plan.Effect,
            Message: null);
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
