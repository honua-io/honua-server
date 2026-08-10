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

    /// <summary>
    /// The feature was edited after the conflict was recorded, so applying the conflict-time
    /// resolution would overwrite that newer edit.
    /// </summary>
    Stale = 7,

    /// <summary>
    /// The synchronization that produced this conflict is still recording it, so its detection state
    /// is provisional and cannot be resolved against yet.
    /// </summary>
    DetectionInFlight = 8,
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

    /// <summary>
    /// How long a claimed-but-unfinalized resolution is assumed to still be in flight. Only a claim
    /// older than this is treated as abandoned and eligible for resume or release, so a second request
    /// from the same operator cannot tear down its own live first attempt (#2430).
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after detection a conflict that still carries no resolution-base generation is treated
    /// as being recorded by an in-flight sync rather than as a legacy record (#2430).
    /// </summary>
    private static readonly TimeSpan DetectionSettleWindow = TimeSpan.FromMinutes(2);

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

        if (conflict.Status == ReplicaConflictStatus.Resolved || conflict.FinalizationPending)
        {
            // Already claimed. That is only terminal if the resolution actually finished — an
            // interrupted one is resumed here rather than reported as already-resolved, which is what
            // stops a post-write failure from stranding the generation and audit evidence (#2430).
            //
            // Deferred-and-unfinalized records route here too: a defer whose audit failed is still
            // owed its evidence, and letting a later action supersede it would lose that permanently.
            return await ResumeOrRejectAsync(conflict, request, activity, cancellationToken)
                .ConfigureAwait(false);
        }

        activity?.SetTag("replicaconflict.type", conflict.ConflictType.ToString());
        activity?.SetTag("replicaconflict.client_edit_applied", conflict.ClientEditApplied);

        if (IsDetectionInFlight(conflict))
        {
            // The originating upload is still applying, so ClientEditApplied has not been decided yet.
            // Resolving now would plan against a provisional snapshot: a keep-server would be recorded
            // as a no-op and then the client edit would commit, leaving the feature holding the client
            // state while the durable resolution claims the server state was kept (#2430).
            Log.ResolutionDetectionInFlight(_logger, conflict.ConflictId);
            activity?.SetStatus(ActivityStatusCode.Error, "detection-in-flight");
            return Failure(
                ReplicaConflictResolutionStatus.DetectionInFlight,
                "This conflict is still being recorded by the synchronization that produced it. Retry the resolution shortly.");
        }

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
            // Either a concurrent operator won the guarded claim, or a previous attempt by THIS
            // resolution claimed the conflict and then failed part-way through finalization. Only the
            // first is genuinely already-resolved; the second is resumable, and short-circuiting it
            // would strand the produced generation and audit evidence forever (#2430).
            return await ResumeOrRejectAsync(outcome.Record.Value, request, activity, cancellationToken)
                .ConfigureAwait(false);
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
            await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            var replanStatus = plan.Rejection == ReplicaConflictResolutionRejection.InvalidRequest
                ? ReplicaConflictResolutionStatus.InvalidRequest
                : ReplicaConflictResolutionStatus.NotApplicable;
            activity?.SetStatus(ActivityStatusCode.Error, plan.RejectionMessage);
            return Failure(replanStatus, plan.RejectionMessage);
        }

        if (plan.Effect != ReplicaConflictResolutionEffect.None && _applier is null)
        {
            await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            Log.ResolutionWriteUnsupported(_logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId);
            return Failure(
                ReplicaConflictResolutionStatus.WriteUnsupported,
                "Applying this resolution requires committing the resolved feature state, which this deployment cannot do: no replica-capable edit pipeline is registered.");
        }

        activity?.SetTag("replicaconflict.effect", plan.Effect.ToString());

        if (RestoresCapturedServerState(request.Action, plan) &&
            await HasUncapturedServerEditAsync(claimed, cancellationToken).ConfigureAwait(false))
        {
            // The pre-apply server snapshot is taken before conflict detection runs, so a server edit
            // landing in that window is detected as the conflict yet missing from ServerStateJson.
            // Writing the snapshot back would silently discard that edit, so the restoration is
            // refused and the operator re-reviews against current state instead (#2430).
            await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            Log.ResolutionServerSnapshotUntrusted(
                _logger, claimed.ConflictId, claimed.ServiceId, claimed.LayerId, claimed.ObjectId);
            activity?.SetStatus(ActivityStatusCode.Error, "server-snapshot-untrusted");
            return Failure(
                ReplicaConflictResolutionStatus.Stale,
                "Another server edit landed while this conflict was being recorded, so the captured server state may not include it. Re-review the conflict against the current server state.");
        }

        // Applies to no-write plans too, not just write-producing ones: an acceptClient whose edit
        // last-write-wins already committed, or a keepServer over an untouched server state, both
        // ASSERT that the conflict-time state is what the row still holds. If a later ordinary edit
        // superseded it, finalizing that assertion records a decision that is no longer true. Only
        // `defer` is exempt — it deliberately asserts nothing about the state (#2430).
        if (request.Action != ReplicaConflictResolutionAction.Defer &&
            await HasPostConflictEditAsync(claimed, cancellationToken).ConfigureAwait(false))
        {
            // The feature moved after this conflict was recorded, so the captured conflict-time state
            // is no longer a safe thing to write: applying it would silently clobber that newer edit.
            await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            Log.ResolutionStale(
                _logger, claimed.ConflictId, claimed.ServiceId, claimed.LayerId, claimed.ObjectId);
            activity?.SetStatus(ActivityStatusCode.Error, "stale");
            return Failure(
                ReplicaConflictResolutionStatus.Stale,
                "This feature was edited after the conflict was recorded, so applying the conflict-time resolution would overwrite that newer edit. Re-review the conflict against the current server state.");
        }

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
                await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
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
                await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionWriteFailed(
                    _logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId, conflict.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, applyResult.FailureMessage);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteFailed,
                    applyResult.FailureMessage ?? "The resolved conflict state could not be committed.");
            }

            // Record that the write landed BEFORE finalization begins, so a resumed attempt knows not
            // to apply it a second time. The marker is not atomic with the edit — the shared edit
            // pipeline owns that transaction — so the resume path additionally re-derives the fact from
            // the change log rather than trusting this flag alone (see ResumeOrRejectAsync).
            await _conflictRepository.TryUpdateFinalizationStateAsync(
                    new ReplicaConflictFinalizationUpdate(
                        claimed.ConflictId,
                        request.Actor,
                        request.Action,
                        WriteCommitted: true,
                        ResolvedServerGeneration: null,
                        Finalized: null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            claimed = claimed with { WriteCommitted = true };
        }

        // Past this point the feature write has committed, so finalization must not be abandoned
        // half-done: a cancelled request token here would throw with the conflict already transitioned
        // to Resolved, leaving the produced generation unpersisted, the audit event unwritten, and
        // retries answered with AlreadyResolved. Finalization therefore runs on an uncancellable token.
        var finalizationToken = CancellationToken.None;

        claimed = await FinalizeAsync(claimed, request, plan, resolution, finalizationToken).ConfigureAwait(false);

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
    /// Whether the conflict's own sync batch is still finalizing its detection state. The batch stamps
    /// <see cref="ReplicaConflictRecord.ResolutionBaseGeneration"/> once it has settled, so a record
    /// that lacks it and was detected moments ago is still in flight and must not be resolved yet.
    /// </summary>
    /// <remarks>
    /// The age test is what separates an in-flight record from a legacy one: conflicts recorded before
    /// the base generation existed also lack it, and blocking those forever would make them
    /// permanently unresolvable. Anything older than the detection window is treated as legacy and
    /// resolvable (with the staleness precondition skipped, as documented on
    /// <see cref="HasPostConflictEditAsync"/>).
    /// </remarks>
    private static bool IsDetectionInFlight(ReplicaConflictRecord conflict)
    {
        if (DateTimeOffset.UtcNow - conflict.DetectedAt >= DetectionSettleWindow)
        {
            // Old enough that whatever state it has is all it will ever have; treat it as settled so
            // legacy records, and records whose enrichment failed outright, stay resolvable.
            return false;
        }

        // The base generation alone is NOT the completion signal: the sync service stamps it before
        // the protocol adapter attaches the client/server envelopes, so a record can look settled while
        // the states a resolution actually reads are still missing (#2430). Detection counts as settled
        // only once both are durable.
        return conflict.ResolutionBaseGeneration is null
            || string.IsNullOrWhiteSpace(conflict.ClientStateJson) && string.IsNullOrWhiteSpace(conflict.ServerStateJson);
    }

    /// <summary>
    /// Decides what a lost claim means. A conflict whose resolution is already finalized is genuinely
    /// already-resolved. One that was claimed but never finalized is a previous attempt of this same
    /// resolution that failed part-way through finalization: it is resumed and completed here rather
    /// than short-circuited, so the produced generation and audit evidence cannot be stranded (#2430).
    /// </summary>
    /// <remarks>
    /// Resume never re-applies the feature write — <see cref="ReplicaConflictRecord.WriteCommitted"/>
    /// records that it already landed, and a record that was claimed without the write committing is
    /// released back to pending so the operator can retry cleanly.
    /// </remarks>
    private async Task<ReplicaConflictResolutionResult> ResumeOrRejectAsync(
        ReplicaConflictRecord existing,
        ReplicaConflictResolutionServiceRequest request,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var sameResolution = existing.ResolutionAction == request.Action
            && string.Equals(existing.ResolvedBy, request.Actor, StringComparison.Ordinal);
        if (!existing.FinalizationPending || !sameResolution)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }



        var plan = ReplicaConflictResolutionPlanner.Plan(existing, request.Action, request.Inputs);
        if (!plan.IsAccepted)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        if (plan.Effect != ReplicaConflictResolutionEffect.None && !existing.WriteCommitted)
        {
            // Releasing is the only destructive branch here, and "same operator, same action,
            // unfinalized" cannot by itself tell a CRASHED attempt from one still in flight. Without a
            // lease, a second request from the same operator would tear down its own live first
            // attempt, which could then still commit its edit while a third request claimed and wrote
            // concurrently. Only a claim that has been silent longer than the lease is treated as
            // abandoned; a live one is reported as already-resolved and left alone (#2430).
            //
            // Resuming finalization needs no such guard: it is idempotent and claim-bound, so a
            // duplicate request converges on the same generation instead of corrupting anything.
            var claimAge = DateTimeOffset.UtcNow - (existing.ResolvedAt ?? DateTimeOffset.UtcNow);
            if (claimAge < ClaimLease)
            {
                Log.ResolutionClaimStillLive(_logger, existing.ConflictId);
                return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
            }

            // No durable evidence the write committed, so the abandoned attempt is released and the
            // conflict returns to pending for a clean retry.
            //
            // Deliberately NOT inferred from the change log: a change-log entry carries no resolution
            // identity, so an ordinary edit by someone else would look identical to "our write landed"
            // and the retry would skip the write and finalize a state that never existed. The
            // double-apply this might seem to risk is already prevented by the staleness precondition
            // — if the write did land, the retry's probe sees it and returns 409 rather than
            // re-applying (#2430).
            await ReleaseClaimAsync(existing, CancellationToken.None).ConfigureAwait(false);
            return Failure(
                ReplicaConflictResolutionStatus.WriteFailed,
                "A previous attempt to resolve this conflict did not commit; the conflict has been returned to pending. Retry the resolution.");
        }

        Log.ResolutionResumed(_logger, existing.ConflictId, existing.ServiceId, existing.LayerId);
        activity?.SetTag("replicaconflict.resumed", true);

        var resumedResolution = new ReplicaConflictResolution(
            existing.ConflictId,
            request.Action,
            request.Actor,
            existing.ResolvedAt ?? DateTimeOffset.UtcNow,
            existing.ResolvedServerGeneration);

        var finalized = await FinalizeAsync(existing, request, plan, resumedResolution, CancellationToken.None)
            .ConfigureAwait(false);

        return new ReplicaConflictResolutionResult(
            ReplicaConflictResolutionStatus.Applied,
            finalized,
            plan.CommittedNewServerState,
            plan.Effect,
            Message: null);
    }

    /// <summary>
    /// Completes a claimed resolution: stamps the generation the committed write produced, records the
    /// audit evidence, and marks the resolution finalized. Idempotent — a resume re-runs it without
    /// re-applying the feature write, and an already-stamped generation is reused rather than replaced.
    /// </summary>
    private async Task<ReplicaConflictRecord> FinalizeAsync(
        ReplicaConflictRecord claimed,
        ReplicaConflictResolutionServiceRequest request,
        ReplicaConflictResolutionPlan plan,
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        long? resolvedGeneration = claimed.ResolvedServerGeneration;
        if (plan.CommittedNewServerState && resolvedGeneration is null)
        {
            // The cursor is read AFTER the write so it names the generation the resolution actually
            // produced, not the one that happened to be current when the request arrived.
            resolvedGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        resolution = resolution with { ResolvedServerGeneration = resolvedGeneration };
        claimed = claimed with { ResolvedServerGeneration = resolvedGeneration };

        // Persist the produced generation first, but leave the resolution PENDING until the audit
        // evidence is durable. Marking it finalized before the audit would make an audit-sink failure
        // permanent: the retry would see a complete resolution and answer already-resolved, and the
        // required evidence would never be written (#2430).
        await _conflictRepository.TryUpdateFinalizationStateAsync(
                new ReplicaConflictFinalizationUpdate(
                    claimed.ConflictId,
                    request.Actor,
                    request.Action,
                    WriteCommitted: null,
                    ResolvedServerGeneration: resolvedGeneration,
                    Finalized: null),
                cancellationToken)
            .ConfigureAwait(false);

        // A resumed finalization can re-emit this event; a duplicated audit record is strictly better
        // than an absent one, and the resolution id makes the duplicate identifiable.
        await RecordAuditAsync(request, claimed, plan, resolution, cancellationToken).ConfigureAwait(false);

        await _conflictRepository.TryUpdateFinalizationStateAsync(
                new ReplicaConflictFinalizationUpdate(
                    claimed.ConflictId,
                    request.Actor,
                    request.Action,
                    WriteCommitted: null,
                    ResolvedServerGeneration: null,
                    Finalized: true),
                cancellationToken)
            .ConfigureAwait(false);

        claimed = claimed with { FinalizationPending = false };

        Log.ResolutionApplied(
            _logger,
            claimed.ConflictId,
            claimed.ServiceId,
            claimed.LayerId,
            claimed.ObjectId,
            request.Action,
            plan.Effect);

        return claimed;
    }

    /// <summary>
    /// Whether this resolution writes the captured <em>server</em> snapshot back to the feature, which
    /// is the only case that depends on that snapshot being complete.
    /// </summary>
    private static bool RestoresCapturedServerState(
        ReplicaConflictResolutionAction action,
        ReplicaConflictResolutionPlan plan)
        => plan.Effect == ReplicaConflictResolutionEffect.WriteFeatureState
            && action is ReplicaConflictResolutionAction.KeepServer
                or ReplicaConflictResolutionAction.RejectClient
                or ReplicaConflictResolutionAction.ChooseGeometry;

    /// <summary>
    /// Whether a server edit may have landed after the pre-apply snapshot was taken but before the
    /// conflict was detected, leaving the captured server state incomplete.
    /// </summary>
    /// <remarks>
    /// The sync batch contributes exactly one change to the conflicting feature (its own applied edit,
    /// or none at all under manual review). More than one change to that feature between the replica's
    /// base generation and the conflict's own generation therefore means a foreign server edit
    /// interleaved, and the snapshot cannot be trusted to contain it. Conflicts lacking either cursor
    /// cannot be checked and are allowed through, consistent with the other precondition.
    /// </remarks>
    private async Task<bool> HasUncapturedServerEditAsync(
        ReplicaConflictRecord conflict,
        CancellationToken cancellationToken)
    {
        if (conflict.StorageLayerId is not { } storageLayerId ||
            conflict.ResolutionBaseGeneration is not { } baseGeneration)
        {
            return false;
        }

        var changes = await _changeTracker
            .GetChangesSinceAsync(conflict.ServerGeneration, [storageLayerId], new HashSet<long> { conflict.ObjectId }, cancellationToken)
            .ConfigureAwait(false);

        return changes.Count(change => change.ObjectId == conflict.ObjectId && change.Generation <= baseGeneration) > 1;
    }

    /// <summary>
    /// Whether the conflicting feature has been edited since the generation the conflict's captured
    /// states describe. That is the precondition a late resolution must satisfy: without it a
    /// keep-server, field merge, geometry choice, or accepted delete reviewed long after detection
    /// silently overwrites a legitimate post-conflict edit (#2430).
    /// </summary>
    /// <remarks>
    /// Requires both the storage-layer id and the resolution-base generation the sync service stamps
    /// on the record. Conflicts recorded before those existed carry neither, and the precondition is
    /// skipped for them rather than blocking every legacy conflict from ever being resolved; the same
    /// applies when the change tracker cannot answer, which is logged.
    /// </remarks>
    private async Task<bool> HasPostConflictEditAsync(
        ReplicaConflictRecord conflict,
        CancellationToken cancellationToken)
    {
        if (conflict.StorageLayerId is not { } storageLayerId ||
            conflict.ResolutionBaseGeneration is not { } baseGeneration)
        {
            Log.ResolutionStalenessUncheckable(_logger, conflict.ConflictId);
            return false;
        }

        var changes = await _changeTracker
            .GetChangesSinceAsync(baseGeneration, [storageLayerId], new HashSet<long> { conflict.ObjectId }, cancellationToken)
            .ConfigureAwait(false);

        return changes.Any(change => change.ObjectId == conflict.ObjectId);
    }

    /// <summary>
    /// Returns a claimed conflict to the pending, reviewable state after its feature write failed to
    /// commit, so the failed attempt does not leave a terminal resolution recorded against a state
    /// that never landed. Always invoked with a fresh cancellation token by its callers, because the
    /// write it is cleaning up after may itself have failed due to cancellation. Any failure to
    /// release is logged rather than thrown — including cancellation — since the caller is already
    /// reporting the write failure and masking that with a second error would lose the real cause.
    /// </summary>
    /// <param name="conflict">
    /// The record returned by the atomic claim, never the pre-claim read. Detection post-processing
    /// can promote <c>ClientEditApplied</c> or attach state envelopes between the two, and releasing
    /// from the stale snapshot would erase those and make the next resolution plan against incorrect
    /// state (#2430).
    /// </param>
    /// <param name="cancellationToken">Fresh token; see the summary.</param>
    private async Task ReleaseClaimAsync(ReplicaConflictRecord conflict, CancellationToken cancellationToken)
    {
        try
        {
            // Claim-bound, not a whole-record write: two retries can both judge an expired claim
            // abandoned, and once the first has released it a third request can claim it again. An
            // unconditional release would clear that replacement claim and let its feature write
            // proceed with no ownership (#2430). A release that no longer matches is a no-op.
            if (conflict is { ResolvedBy: { } resolvedBy, ResolutionAction: { } action, ResolvedAt: { } resolvedAt })
            {
                await _conflictRepository
                    .TryReleaseClaimAsync(conflict.ConflictId, resolvedBy, action, resolvedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
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

        [LoggerMessage(EventId = 7749, Level = LogLevel.Warning,
            Message = "Refused to resolve replica conflict {ConflictId} for service {ServiceId} layer {LayerId} objectId {ObjectId}: the feature was edited after the conflict was recorded")]
        public static partial void ResolutionStale(
            ILogger logger, string conflictId, string serviceId, int layerId, long objectId);

        [LoggerMessage(EventId = 7750, Level = LogLevel.Information,
            Message = "Resuming finalization of replica conflict {ConflictId} for service {ServiceId} layer {LayerId} after an interrupted resolution")]
        public static partial void ResolutionResumed(
            ILogger logger, string conflictId, string serviceId, int layerId);

        [LoggerMessage(EventId = 7752, Level = LogLevel.Information,
            Message = "Replica conflict {ConflictId} is claimed by an attempt that is still within the lease window; reporting already-resolved rather than resuming or releasing it")]
        public static partial void ResolutionClaimStillLive(ILogger logger, string conflictId);

        [LoggerMessage(EventId = 7754, Level = LogLevel.Warning,
            Message = "Refused to restore the captured server state for replica conflict {ConflictId} (service {ServiceId} layer {LayerId} objectId {ObjectId}): another server edit landed while the conflict was being recorded, so the snapshot may be incomplete")]
        public static partial void ResolutionServerSnapshotUntrusted(
            ILogger logger, string conflictId, string serviceId, int layerId, long objectId);

        [LoggerMessage(EventId = 7753, Level = LogLevel.Information,
            Message = "Refused to resolve replica conflict {ConflictId}: the synchronization that produced it is still recording its detection state")]
        public static partial void ResolutionDetectionInFlight(ILogger logger, string conflictId);

        [LoggerMessage(EventId = 7751, Level = LogLevel.Debug,
            Message = "Replica conflict {ConflictId} carries no storage layer / resolution-base generation, so the post-conflict-edit precondition was skipped")]
        public static partial void ResolutionStalenessUncheckable(ILogger logger, string conflictId);

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
