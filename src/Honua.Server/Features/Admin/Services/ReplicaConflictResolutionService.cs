// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The shared edit pipeline could not determine whether the resolved state committed. The
    /// resolution stays claimed and resumable so a retry re-applies it idempotently, because
    /// releasing it would let the next attempt see this resolution's own change as a post-conflict
    /// edit and strand the conflict as permanently stale.
    /// </summary>
    WriteOutcomeUnknown = 9,
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
    /// How long after detection an incomplete conflict is still plausibly being recorded by an
    /// in-flight sync. Used only to word the rejection: inside the window a retry may well succeed,
    /// past it the originating sync did not finish and the operator needs to re-run it. Completeness,
    /// not age, decides whether the conflict is resolvable (#2430).
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
                DateTimeOffset.UtcNow - conflict.DetectedAt < DetectionSettleWindow
                    ? "This conflict is still being recorded by the synchronization that produced it. Retry the resolution shortly."
                    : "The synchronization that produced this conflict did not finish recording it, so the conflict-time state a resolution needs is missing. It cannot be resolved safely; re-run the synchronization for this replica.");
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
            ResolvedServerGeneration: null,
            ResolutionInputHash: ComputeInputHash(request));

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

        // Snapshot the row BEFORE the staleness probe and carry that token into the write. Capturing it
        // afterwards would already describe an edit the probe did not see, and the write would then
        // accept the very change the probe exists to reject; binding both to one snapshot makes them a
        // single decision (#2430).
        string? expectedStateToken = null;
        var expectedRowAbsent = false;
        var tokenPersisted = true;
        bool stale;
        try
        {
            if (_applier is not null &&
                plan.Effect != ReplicaConflictResolutionEffect.None &&
                claimed.StorageLayerId is { } tokenLayerId)
            {
                var snapshot = await _applier
                    .CaptureStateTokenAsync(tokenLayerId, claimed.ObjectId, cancellationToken)
                    .ConfigureAwait(false);
                expectedStateToken = snapshot.StateToken;
                expectedRowAbsent = !snapshot.Exists;

                // Durable IMMEDIATELY, before the probe runs. Capture plus probe can outlast the lease,
                // and a recovery that took the claim over in that window would re-apply against a still
                // null persisted token — i.e. with no precondition at all — and overwrite whatever
                // arrived meanwhile (#2430).
                if (expectedStateToken is { Length: > 0 })
                {
                    tokenPersisted = await _conflictRepository.TryUpdateFinalizationStateAsync(
                            new ReplicaConflictFinalizationUpdate(
                                claimed.ConflictId,
                                request.Actor,
                                request.Action,
                                claimed.ResolvedAt ?? default,
                                WriteCommitted: null,
                                ResolvedServerGeneration: null,
                                Finalized: null,
                                PreWriteStateToken: expectedStateToken),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    claimed = claimed with { PreWriteStateToken = expectedStateToken };
                }
            }

            stale = request.Action != ReplicaConflictResolutionAction.Defer &&
                await HasPostConflictEditAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The claim already moved the record out of the reviewable state, but nothing has been
            // written and — crucially — the staleness question is still unanswered. Leaving the claim
            // would let a retry resume straight to finalization (no-write plans) or take the claim over
            // with the check explicitly skipped, either way accepting or overwriting a post-conflict
            // edit this probe was about to detect. Release with a fresh token: the request's own token
            // is typically the thing that was cancelled (#2430).
            await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }

        if (!tokenPersisted)
        {
            // The guarded update matched nothing, so this request no longer owns the claim: a retry
            // took it over while the capture or probe was running. Continuing would write under an
            // ownership another attempt now holds (#2430).
            Log.ResolutionClaimLost(_logger, claimed.ConflictId);
            activity?.SetStatus(ActivityStatusCode.Error, "claim-lost");
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        if (stale)
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
                applyResult = await ApplyResolutionWriteAsync(
                        claimed, plan, expectedStateToken, expectedRowAbsent, cancellationToken)
                    .ConfigureAwait(false);
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

            if (applyResult.PreconditionFailed)
            {
                // The write's own precondition caught an edit that arrived between the staleness probe
                // and the write transaction, which is the window the probe alone cannot cover. Nothing
                // was written, so the claim is released and the conflict goes back to reviewable with
                // the same answer the probe would have given (#2430).
                await ReleaseClaimAsync(claimed, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionStale(
                    _logger, claimed.ConflictId, claimed.ServiceId, claimed.LayerId, claimed.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, "stale");
                return Failure(ReplicaConflictResolutionStatus.Stale, "This feature was edited after the conflict was recorded, so applying the conflict-time resolution would overwrite that newer edit. Re-review the conflict against the current server state.");
            }

            if (applyResult.CommitOutcomeUnknown)
            {
                // Deliberately NOT released. The pipeline is saying the write may have landed, so the
                // conflict is neither resolved nor safely reviewable: it stays claimed with
                // WriteCommitted=false, which is exactly the resumable state, and a retry of the same
                // request re-applies the (idempotent) write and finishes finalization. Releasing here
                // would let the next attempt see this resolution's own change as a post-conflict edit
                // and return Stale forever (#2430).
                // Back-date the claim so the operator's retry can resume immediately instead of waiting
                // out the lease. The lease exists to stop a second dispatch while an attempt is still
                // running; here the attempt has demonstrably finished, it just cannot say how.
                await ExpireClaimLeaseAsync(claimed, request).ConfigureAwait(false);
                Log.ResolutionWriteOutcomeUnknown(
                    _logger, conflict.ConflictId, conflict.ServiceId, conflict.LayerId, conflict.ObjectId);
                activity?.SetStatus(ActivityStatusCode.Error, "write outcome unknown");
                return Failure(
                    ReplicaConflictResolutionStatus.WriteOutcomeUnknown,
                    "The resolved conflict state may or may not have been committed: the storage layer did not acknowledge the transaction. The resolution stays claimed - retry the same request to complete it.");
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
            // The guarded update returns false when the row no longer carries this claim — a slow write
            // outlived the lease and the claim was released or replaced. Reporting success then would
            // describe a resolution this request no longer owns, so it stops here (#2430).
            var markedCommitted = await _conflictRepository.TryUpdateFinalizationStateAsync(
                    new ReplicaConflictFinalizationUpdate(
                        claimed.ConflictId,
                        request.Actor,
                        request.Action,
                        claimed.ResolvedAt ?? default,
                        WriteCommitted: true,
                        ResolvedServerGeneration: null,
                        Finalized: null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!markedCommitted)
            {
                Log.ResolutionClaimLost(_logger, claimed.ConflictId);
                activity?.SetStatus(ActivityStatusCode.Error, "claim-lost");
                return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
            }

            claimed = claimed with { WriteCommitted = true };
        }

        // Past this point the feature write has committed, so finalization must not be abandoned
        // half-done: a cancelled request token here would throw with the conflict already transitioned
        // to Resolved, leaving the produced generation unpersisted, the audit event unwritten, and
        // retries answered with AlreadyResolved. Finalization therefore runs on an uncancellable token.
        var finalizationToken = CancellationToken.None;

        ReplicaConflictRecord? finalized;
        try
        {
            finalized = await FinalizeAsync(claimed, request, plan, resolution, finalizationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Finalization failed, so the claim is deliberately retained and resumable. Back-date its
            // lease first: the attempt has demonstrably ended, and leaving a live lease would make the
            // operator's own retry look like a concurrent duplicate for the length of it (#2430).
            await ExpireClaimLeaseAsync(claimed, request).ConfigureAwait(false);
            throw;
        }

        if (finalized is not { } completed)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        claimed = completed;

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
    /// A record's storage-layer id is what separates a current record from a legacy one: every current
    /// sync stamps it at insert, and conflicts recorded before that state existed will never gain it.
    /// Legacy records are therefore treated as settled and stay resolvable (with the staleness
    /// precondition skipped, as documented on <see cref="HasPostConflictEditAsync"/>), while a current
    /// record is judged on whether it actually carries the state a resolution reads.
    /// </remarks>
    private static bool IsDetectionInFlight(ReplicaConflictRecord conflict)
    {
        if (conflict.StorageLayerId is null)
        {
            // A genuine legacy record: written before detection persisted any of this state, so it will
            // never gain it. Blocking those forever would make every pre-existing conflict permanently
            // unresolvable, so they are treated as settled — with the staleness precondition skipped,
            // as documented on HasPostConflictEditAsync.
            return false;
        }

        // Recorded by a current sync: completeness itself is the signal, so a record that already has
        // everything a resolution reads is settled immediately, and one that does not stays blocked no
        // matter how old it is. Ageing an incomplete record out is what skipped the staleness
        // precondition and let a resolution overwrite edits made after an aborted sync (#2430).
        return IsDetectionIncomplete(conflict);
    }

    /// <summary>
    /// Whether a conflict recorded by a current sync is missing state a resolution reads.
    /// </summary>
    /// <remarks>
    /// The base generation alone is NOT the completion signal: the sync service stamps it before the
    /// protocol adapter attaches the server envelope, so a record can look settled while a state a
    /// resolution actually reads is still missing (#2430). Both envelopes must be durable: the client
    /// side is written with the record while the server side is attached afterwards, so "either one
    /// present" let an operator resolve in between and run a field merge or geometry choice against the
    /// client envelope alone, overwriting server attributes that were supposed to be preserved.
    /// <para>
    /// A record that is still incomplete after the settle window is not "settled" — its originating
    /// sync was cut short between the insert and the enrichment. Ageing it out would skip the staleness
    /// precondition (there is no base generation to check against) and let a resolution overwrite edits
    /// made after the aborted sync, so it stays blocked until the state it needs exists.
    /// </para>
    /// </remarks>
    private static bool IsDetectionIncomplete(ReplicaConflictRecord conflict)
    {
        if (conflict.ResolutionBaseGeneration is null)
        {
            return true;
        }

        // Which envelopes are OWED depends on the conflict, because a structural conflict legitimately
        // has only one side: a client delete carries no client feature state, and a feature the server
        // already deleted has no server state to capture. Demanding both universally would leave every
        // delete conflict blocked forever, even though the planner supports accepting a withheld client
        // delete and keeping a server deletion.
        var clientOwed = conflict.ConflictType is not ReplicaConflictType.DeleteUpdate;
        var serverOwed = conflict.ConflictType is not ReplicaConflictType.UpdateDelete;

        // Conflicts outside the feature-state taxonomy carry no comparable envelopes at all; the base
        // generation is the only completion signal they have.
        if (conflict.ConflictType is ReplicaConflictType.DuplicateInsert
            or ReplicaConflictType.Attachment
            or ReplicaConflictType.Relationship)
        {
            clientOwed = false;
            serverOwed = false;
        }

        return (clientOwed && string.IsNullOrWhiteSpace(conflict.ClientStateJson))
            || (serverOwed && string.IsNullOrWhiteSpace(conflict.ServerStateJson));
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
        // Operator and action alone are not the resolution's identity: a mergeFields carrying different
        // values, or a chooseGeometry naming the other side, is a DIFFERENT requested state. Matching
        // only actor and action let such a retry finalize the earlier committed write while the
        // response and audit described the newly requested state, which never landed (#2430). The hash
        // is null only on claims taken before it existed, which fall back to the old check.
        var sameResolution = existing.ResolutionAction == request.Action
            && string.Equals(existing.ResolvedBy, request.Actor, StringComparison.Ordinal)
            && (existing.ResolutionInputHash is null
                || string.Equals(existing.ResolutionInputHash, ComputeInputHash(request), StringComparison.Ordinal));
        if (!existing.FinalizationPending || !sameResolution)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        // The lease gates EVERY resume, not just the ones that re-dispatch a write. "Same operator,
        // same action" cannot distinguish a crashed attempt from one still in flight, and a no-write
        // plan used to skip straight to finalization: a retry arriving while the first request was
        // still inside its staleness probe would audit and report Applied, and the original — finding a
        // post-conflict edit — would then release the same timestamp-bound claim back to Pending,
        // leaving a resolution reported as applied against a conflict that is reviewable again (#2430).
        var claimAge = DateTimeOffset.UtcNow - (existing.ResolvedAt ?? DateTimeOffset.UtcNow);
        if (claimAge < ClaimLease)
        {
            Log.ResolutionClaimStillLive(_logger, existing.ConflictId);
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }


        var plan = ReplicaConflictResolutionPlanner.Plan(existing, request.Action, request.Inputs);
        if (!plan.IsAccepted)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        if (plan.Effect != ReplicaConflictResolutionEffect.None && !existing.WriteCommitted)
        {
            // The claim is unfinalized and the committed-write marker is absent, so the previous
            // attempt died somewhere around its write and we cannot tell from durable state whether it
            // landed. Both guesses are wrong: releasing strands the conflict (the retry's staleness
            // probe would see the resolution's OWN change and return Stale forever), and assuming it
            // landed can finalize a state that never existed.
            //
            // The write itself is the tie-breaker, because it is idempotent by construction: it sets a
            // known target state or deletes a known feature. So the attempt is simply re-run, with the
            // staleness probe skipped for exactly this case — the change it would trip over may be our
            // own (#2430).
            //
            // The lease was already checked above, for every resume. Take ownership atomically before
            // re-dispatching. Recovery re-applies the write, so two
            // retries that both judged this claim abandoned would otherwise both re-apply, and a
            // failure in one would release a claim the other had already committed against (#2430).
            var takenOverAt = DateTimeOffset.UtcNow;
            var tookOver = await _conflictRepository.TryTakeOverClaimAsync(
                    existing.ConflictId,
                    request.Actor,
                    request.Action,
                    existing.ResolvedAt ?? default,
                    takenOverAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!tookOver)
            {
                Log.ResolutionClaimStillLive(_logger, existing.ConflictId);
                return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
            }

            existing = existing with { ResolvedAt = takenOverAt };

            Log.ResolutionWriteReapplied(_logger, existing.ConflictId);
            if (existing.StorageLayerId is not null && existing.PreWriteStateToken is null)
            {
                // The claim's pre-write phase never became durable, so there is no snapshot to bind the
                // recovered write to — and recovery skips the staleness probe by design. Writing with no
                // precondition would overwrite whatever is in the row now (#2430).
                await ReleaseClaimAsync(existing, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionStale(
                    _logger, existing.ConflictId, existing.ServiceId, existing.LayerId, existing.ObjectId);
                return Failure(
                    ReplicaConflictResolutionStatus.Stale,
                    "The interrupted resolution did not record the feature state it was claimed against, so it cannot be safely re-applied. Re-review the conflict against the current server state.");
            }

            // The RETAINED token, never one derived now. Recovery skips the staleness probe on purpose
            // (the change it would trip over may be this resolution's own write), so a retry-time token
            // would describe whatever is in the row at this moment — including a normal edit that landed
            // during the lease — and the precondition would then happily overwrite it (#2430).
            var reapplied = await ApplyResolutionWriteAsync(
                    existing, plan, existing.PreWriteStateToken, expectedRowAbsent: false, cancellationToken)
                .ConfigureAwait(false);
            if (reapplied.PreconditionFailed)
            {
                // The row is no longer what it was when this resolution was claimed. That is either this
                // resolution's own write (its marker never landed) or a foreign edit during the lease,
                // and the collapsed change log cannot tell them apart. Refusing to write is the only
                // answer that cannot destroy data — but simply releasing would strand the conflict: if
                // the write DID land, every later attempt's staleness probe trips over this resolution's
                // own change and returns Stale forever. So the conflict is re-baselined onto the state
                // that is actually there before being released, and the operator re-reviews against it
                // (#2430).
                await ReleaseClaimAsync(existing, CancellationToken.None).ConfigureAwait(false);
                await RebaselineAsync(existing, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionStale(
                    _logger, existing.ConflictId, existing.ServiceId, existing.LayerId, existing.ObjectId);
                return Failure(ReplicaConflictResolutionStatus.Stale, "This feature was edited after the conflict was recorded, so applying the conflict-time resolution would overwrite that newer edit. Re-review the conflict against the current server state.");
            }

            if (reapplied.CommitOutcomeUnknown)
            {
                // Same reasoning as the first attempt: keep the claim so the next retry resumes.
                await ExpireClaimLeaseAsync(existing, request).ConfigureAwait(false);
                Log.ResolutionWriteOutcomeUnknown(
                    _logger, existing.ConflictId, existing.ServiceId, existing.LayerId, existing.ObjectId);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteOutcomeUnknown,
                    "The resolved conflict state may or may not have been committed: the storage layer did not acknowledge the transaction. The resolution stays claimed - retry the same request to complete it.");
            }

            if (!reapplied.Applied)
            {
                await ReleaseClaimAsync(existing, CancellationToken.None).ConfigureAwait(false);
                return Failure(
                    ReplicaConflictResolutionStatus.WriteFailed,
                    reapplied.FailureMessage ?? "The resolved conflict state could not be committed.");
            }

            var resumedMarked = await _conflictRepository.TryUpdateFinalizationStateAsync(
                    new ReplicaConflictFinalizationUpdate(
                        existing.ConflictId,
                        request.Actor,
                        request.Action,
                        existing.ResolvedAt ?? default,
                        WriteCommitted: true,
                        ResolvedServerGeneration: null,
                        Finalized: null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!resumedMarked)
            {
                // The re-applied write outlived the lease and another retry restamped resolved_at.
                // Continuing would finalize a row this request no longer owns, and if the replacement
                // then fails and releases its claim the conflict is pending behind a feature change
                // this request committed (#2430).
                Log.ResolutionClaimLost(_logger, existing.ConflictId);
                return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
            }

            existing = existing with { WriteCommitted = true };
        }

        if (plan.Effect == ReplicaConflictResolutionEffect.None)
        {
            // No-write resumes take ownership too. Finalization and release are both bound to the claim
            // timestamp, so stamping a new one fences the attempt being taken over: it can no longer
            // release this conflict back to Pending after this resume has reported it applied (#2430).
            var noWriteTakeoverAt = DateTimeOffset.UtcNow;
            var tookOverNoWrite = await _conflictRepository.TryTakeOverClaimAsync(
                    existing.ConflictId,
                    request.Actor,
                    request.Action,
                    existing.ResolvedAt ?? default,
                    noWriteTakeoverAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!tookOverNoWrite)
            {
                Log.ResolutionClaimStillLive(_logger, existing.ConflictId);
                return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
            }

            existing = existing with { ResolvedAt = noWriteTakeoverAt };

            // The takeover says nothing about whether the attempt being replaced had finished its
            // staleness probe, and a no-write resume goes straight to audit and finalization. Re-run the
            // probe under the new ownership so a post-conflict edit cannot be recorded as resolved by a
            // retry while the original was about to report it stale (#2430).
            if (request.Action != ReplicaConflictResolutionAction.Defer &&
                await HasPostConflictEditAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                await ReleaseClaimAsync(existing, CancellationToken.None).ConfigureAwait(false);
                Log.ResolutionStale(
                    _logger, existing.ConflictId, existing.ServiceId, existing.LayerId, existing.ObjectId);
                return Failure(
                    ReplicaConflictResolutionStatus.Stale,
                    "This feature was edited after the conflict was recorded, so applying the conflict-time resolution would overwrite that newer edit. Re-review the conflict against the current server state.");
            }
        }

        Log.ResolutionResumed(_logger, existing.ConflictId, existing.ServiceId, existing.LayerId);
        activity?.SetTag("replicaconflict.resumed", true);

        var resumedResolution = new ReplicaConflictResolution(
            existing.ConflictId,
            request.Action,
            request.Actor,
            existing.ResolvedAt ?? DateTimeOffset.UtcNow,
            existing.ResolvedServerGeneration);

        ReplicaConflictRecord? finalized;
        try
        {
            finalized = await FinalizeAsync(existing, request, plan, resumedResolution, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Same as the first attempt: keep the claim but end its lease so the next retry resumes.
            await ExpireClaimLeaseAsync(existing, request).ConfigureAwait(false);
            throw;
        }

        if (finalized is not { } completed)
        {
            return Failure(ReplicaConflictResolutionStatus.AlreadyResolved, message: null);
        }

        return new ReplicaConflictResolutionResult(
            ReplicaConflictResolutionStatus.Applied,
            completed,
            plan.CommittedNewServerState,
            plan.Effect,
            Message: null);
    }

    /// <summary>
    /// Completes a claimed resolution: stamps the generation the committed write produced, records the
    /// audit evidence, and marks the resolution finalized. Idempotent — a resume re-runs it without
    /// re-applying the feature write, and an already-stamped generation is reused rather than replaced.
    /// </summary>
    /// <returns>The finalized record, or null when ownership of the claim was lost mid-finalization.</returns>
    private async Task<ReplicaConflictRecord?> FinalizeAsync(
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
        var stampedGeneration = await _conflictRepository.TryUpdateFinalizationStateAsync(
                new ReplicaConflictFinalizationUpdate(
                    claimed.ConflictId,
                    request.Actor,
                    request.Action,
                    claimed.ResolvedAt ?? default,
                    WriteCommitted: null,
                    ResolvedServerGeneration: resolvedGeneration,
                    Finalized: null),
                cancellationToken)
            .ConfigureAwait(false);
        if (!stampedGeneration)
        {
            // Ownership was lost between the write and finalization; the row now belongs to another
            // claim, so this request must not write its audit evidence or report success over it.
            Log.ResolutionClaimLost(_logger, claimed.ConflictId);
            return null;
        }

        // A resumed finalization can re-emit this event; a duplicated audit record is strictly better
        // than an absent one, and the resolution id makes the duplicate identifiable.
        await RecordAuditAsync(request, claimed, plan, resolution, cancellationToken).ConfigureAwait(false);

        var markedFinalized = await _conflictRepository.TryUpdateFinalizationStateAsync(
                new ReplicaConflictFinalizationUpdate(
                    claimed.ConflictId,
                    request.Actor,
                    request.Action,
                    claimed.ResolvedAt ?? default,
                    WriteCommitted: null,
                    ResolvedServerGeneration: null,
                    Finalized: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (!markedFinalized)
        {
            // Same guard as the generation stamp above, and it matters just as much: a slow audit can
            // outlive the lease and a retry can take the claim over by restamping resolved_at. Clearing
            // the local pending flag and reporting Applied would then describe a resolution this
            // request no longer owns, and if the replacement fails the durable conflict stays
            // unfinalized behind a success response (#2430).
            Log.ResolutionClaimLost(_logger, claimed.ConflictId);
            return null;
        }

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
    /// Re-points a conflict at the state the feature actually holds now: refreshes the captured server
    /// envelope and moves the resolution base generation to the current cursor.
    /// </summary>
    /// <remarks>
    /// Used when a resolution's own write can no longer be attributed, where releasing alone would
    /// strand the conflict — a later attempt's staleness probe would trip over this resolution's own
    /// change and answer Stale forever. Re-baselining makes the conflict reviewable against reality
    /// instead. Runs AFTER the claim is released, because the detection-state update is guarded on the
    /// conflict being pending — the same guard that stops detection post-processing from reopening a
    /// resolved conflict. Best-effort: a failure here leaves the conflict as it was (#2430).
    /// </remarks>
    private async Task RebaselineAsync(ReplicaConflictRecord conflict, CancellationToken cancellationToken)
    {
        if (_applier is null || conflict.StorageLayerId is not { } storageLayerId)
        {
            return;
        }

        try
        {
            // Generation first, then the state, then a re-read to prove the state did not move between
            // them. Sampling the generation last would let an edit landing in between be inside the new
            // base while absent from the refreshed envelope, so a later resolution would find nothing
            // post-base and overwrite that edit with the stale snapshot (#2430).
            var generation = await _changeTracker.GetCurrentGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await _applier
                .CaptureStateTokenAsync(storageLayerId, conflict.ObjectId, cancellationToken)
                .ConfigureAwait(false);
            var confirmation = await _applier
                .CaptureStateTokenAsync(storageLayerId, conflict.ObjectId, cancellationToken)
                .ConfigureAwait(false);
            if (confirmation.Exists != snapshot.Exists ||
                !string.Equals(confirmation.StateToken, snapshot.StateToken, StringComparison.Ordinal))
            {
                // The row moved while it was being re-baselined. Leave the conflict as it was rather
                // than pairing an envelope with a generation that does not describe it; the operator
                // gets the unchanged (safe, possibly stale) record instead.
                Log.ResolutionRebaselineSkipped(_logger, conflict.ConflictId);
                return;
            }

            await _conflictRepository.TryUpdateDetectionStateAsync(
                    new ReplicaConflictDetectionUpdate(
                        conflict.ConflictId,
                        ConflictType: null,
                        ClientStateJson: null,
                        ServerStateJson: snapshot.StateJson,
                        // The refreshed envelope IS the current server side, so the attribution flags
                        // that described the original upload no longer hold. Leaving ClientEditApplied
                        // true would let a later acceptClient take its no-op shortcut and report success
                        // while the row still held the server state.
                        ClientEditApplied: false,
                        ResolutionBaseGeneration: generation,
                        ClientEditOutcomeUnknown: false,
                        ClientEditSuperseded: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ResolutionRebaselineFailed(_logger, conflict.ConflictId, ex);
        }
    }

    /// <summary>
    /// Back-dates a retained claim past its lease so the operator's own retry can resume it at once.
    /// Used only for an indeterminate write, where the attempt has finished but cannot say whether it
    /// committed: the claim must stay (releasing it strands the conflict as permanently stale if the
    /// write did land), yet holding a live lease would make every retry look like a concurrent
    /// duplicate for the length of the lease (#2430).
    /// </summary>
    private Task<bool> ExpireClaimLeaseAsync(
        ReplicaConflictRecord claimed,
        ReplicaConflictResolutionServiceRequest request)
        => _conflictRepository.TryTakeOverClaimAsync(
            claimed.ConflictId,
            request.Actor,
            request.Action,
            claimed.ResolvedAt ?? default,
            DateTimeOffset.UtcNow - ClaimLease - TimeSpan.FromSeconds(1),
            CancellationToken.None);

    /// <summary>
    /// A stable hash of everything that determines which state a resolution writes: the action plus the
    /// operator-supplied field values and geometry side. Used to prove that a resume is completing the
    /// same request that was claimed, rather than finalizing an earlier write under a new request's
    /// description (#2430).
    /// </summary>
    private static string ComputeInputHash(ReplicaConflictResolutionServiceRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Action).Append('\u001f');

        // Only the inputs the selected action actually consumes. The others are documented as ignored,
        // so folding them in would make a resumable request differ from the semantically identical
        // minimal one — leaving the unfinalized resolution stranded (#2430).
        if (request.Action == ReplicaConflictResolutionAction.ChooseGeometry)
        {
            builder.Append(request.Inputs.GeometrySource?.Trim().ToLowerInvariant()).Append('\u001f');
        }

        if (request.Action == ReplicaConflictResolutionAction.MergeFields &&
            request.Inputs.FieldValues is { Count: > 0 } fieldValues)
        {
            // Field names are lowered and the pairs ordered, so a request that produces an identical
            // planned edit hashes identically: the planner matches operator-supplied names to schema
            // fields case-insensitively, and a payload's key order is not meaningful. Without both,
            // retrying `status` as `STATUS` looked like a different request and stranded the claim's
            // finalization (#2430). Value text is the secondary sort so two keys differing only by
            // case stay order-independent too.
            foreach (var field in fieldValues
                .Select(field => (Key: field.Key.ToLowerInvariant(), Value: field.Value.GetRawText()))
                .OrderBy(field => field.Key, StringComparer.Ordinal)
                .ThenBy(field => field.Value, StringComparer.Ordinal))
            {
                builder.Append(field.Key).Append('=').Append(field.Value).Append('\u001e');
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Dispatches the planned resolution write through the shared edit pipeline. Idempotent by
    /// construction — it sets a known target state or deletes a known feature — which is what lets an
    /// interrupted attempt simply re-run it rather than having to guess whether it landed.
    /// </summary>
    private Task<ReplicaConflictApplyResult> ApplyResolutionWriteAsync(
        ReplicaConflictRecord conflict,
        ReplicaConflictResolutionPlan plan,
        string? expectedStateToken,
        bool expectedRowAbsent,
        CancellationToken cancellationToken)
        => _applier!.ApplyAsync(
            new ReplicaConflictResolutionCommand(
                conflict.ServiceId,
                conflict.LayerId,
                conflict.ObjectId,
                plan.Effect,
                plan.FeatureStateJson,
                conflict.StorageLayerId,
                expectedStateToken,
                expectedRowAbsent),
            cancellationToken);

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

        [LoggerMessage(EventId = 7759, Level = LogLevel.Warning,
            Message = "Replica conflict {ConflictId} was not re-baselined: the feature changed while its state was being refreshed")]
        public static partial void ResolutionRebaselineSkipped(ILogger logger, string conflictId);

        [LoggerMessage(EventId = 7758, Level = LogLevel.Warning,
            Message = "Replica conflict {ConflictId} could not be re-baselined onto the current server state")]
        public static partial void ResolutionRebaselineFailed(ILogger logger, string conflictId, Exception exception);

        [LoggerMessage(EventId = 7757, Level = LogLevel.Error,
            Message = "Replica conflict {ConflictId} for service {ServiceId} layer {LayerId} objectId {ObjectId} stays claimed: the storage layer did not acknowledge the resolution write, so it may or may not have committed")]
        public static partial void ResolutionWriteOutcomeUnknown(
            ILogger logger, string conflictId, string serviceId, int layerId, long objectId);

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

        [LoggerMessage(EventId = 7755, Level = LogLevel.Warning,
            Message = "Re-applying the resolution write for replica conflict {ConflictId}: a previous attempt left no committed-write marker, and the write is idempotent")]
        public static partial void ResolutionWriteReapplied(ILogger logger, string conflictId);

        [LoggerMessage(EventId = 7756, Level = LogLevel.Warning,
            Message = "Replica conflict {ConflictId} no longer carries this request's claim; abandoning the resolution rather than reporting it applied")]
        public static partial void ResolutionClaimLost(ILogger logger, string conflictId);

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
