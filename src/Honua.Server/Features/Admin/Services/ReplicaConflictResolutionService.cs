// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal readonly record struct ReplicaConflictResolutionResult(
    ReplicaConflictResolutionStatus Status,
    ReplicaConflictRecord? Record,
    bool CommittedNewServerState,
    ReplicaConflictResolutionEffect Effect,
    string? Message);

internal enum ReplicaConflictResolutionStatus
{
    Applied = 0,

    NotFound = 1,

    AlreadyResolved = 2,

    InvalidRequest = 3,

    NotApplicable = 4,

    WriteUnsupported = 5,

    WriteFailed = 6,

    Stale = 7,

    // A conflict whose detection state is still being recorded cannot be resolved yet.
    DetectionInFlight = 8,

    // An indeterminate commit stays claimed so a retry reapplies it idempotently; releasing it would let the next attempt treat this write as a later edit.
    WriteOutcomeUnknown = 9,
}

// The feature write commits before the resolution is recorded, and a failed write leaves the conflict pending.
internal sealed partial class ReplicaConflictResolutionService
{
    private static readonly ActivitySource _activitySource = new("Honua.Server.ReplicaConflicts");

    // A claim younger than this lease is still the caller's own attempt and is not torn down by a second request.
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan DetectionSettleWindow = TimeSpan.FromMinutes(2);

    private readonly IReplicaConflictRepository _conflictRepository;
    private readonly IChangeTracker _changeTracker;
    private readonly IAuditLog _auditLog;
    private readonly IReplicaConflictResolutionApplier? _applier;
    private readonly ILogger<ReplicaConflictResolutionService> _logger;

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
                    .CaptureStateTokenAsync(
                        claimed.ServiceId,
                        claimed.LayerId,
                        tokenLayerId,
                        claimed.ObjectId,
                        cancellationToken)
                    .ConfigureAwait(false);
                expectedStateToken = snapshot.StateToken;
                expectedRowAbsent = !snapshot.Exists;

                // Durable IMMEDIATELY, before the probe runs. Capture plus probe can outlast the lease,
                // and a recovery that took the claim over in that window would re-apply against a still
                // null persisted token — i.e. with no precondition at all — and overwrite whatever
                // arrived meanwhile (#2430).
                tokenPersisted = await _conflictRepository.TryUpdateFinalizationStateAsync(
                        new ReplicaConflictFinalizationUpdate(
                            claimed.ConflictId,
                            request.Actor,
                            request.Action,
                            claimed.ResolvedAt ?? default,
                            WriteCommitted: null,
                            ResolvedServerGeneration: null,
                            Finalized: null,
                            PreWriteStateToken: expectedStateToken,
                            PreWriteRowAbsent: expectedRowAbsent),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                claimed = claimed with
                {
                    PreWriteStateToken = expectedStateToken,
                    PreWriteRowAbsent = expectedRowAbsent,
                };
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
            if (claimed.ClientEditOutcomeUnknown)
            {
                // The post-base change may be the originating upload itself: its lost acknowledgement
                // is exactly why this record cannot classify the outcome. Releasing it unchanged would
                // make every retry rediscover that same change and answer Stale forever. Re-point the
                // now-pending record at a stable snapshot of the state that actually exists, clear the
                // ambiguous attribution, and require the operator to review that current state once.
                await RebaselineAsync(claimed, CancellationToken.None).ConfigureAwait(false);
            }
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

    // A current record that has not settled must not be resolved; a legacy record with no storage-layer id is treated as settled.
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

    // Both the client and server envelopes must be durable before a resolution runs, and an incomplete current record stays blocked after the settle window.
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
            or ReplicaConflictType.Relationship
            or ReplicaConflictType.DeleteDelete)
        {
            clientOwed = false;
            serverOwed = false;
        }

        return (clientOwed && string.IsNullOrWhiteSpace(conflict.ClientStateJson))
            || (serverOwed && string.IsNullOrWhiteSpace(conflict.ServerStateJson));
    }

    // A claimed resolution that never finalized is resumed without repeating a feature write that already landed.
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
            if (existing.StorageLayerId is not null &&
                existing.PreWriteStateToken is null &&
                existing.PreWriteRowAbsent != true)
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
                    existing,
                    plan,
                    existing.PreWriteStateToken,
                    expectedRowAbsent: existing.PreWriteRowAbsent == true,
                    cancellationToken)
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

    // Re-baselining runs after the claim is released, because the detection update only applies while the conflict is pending.
    private async Task RebaselineAsync(ReplicaConflictRecord conflict, CancellationToken cancellationToken)
    {
        if (_applier is null || conflict.StorageLayerId is not { } storageLayerId)
        {
            return;
        }

        try
        {
            // State, then generation, then a re-read that proves the state did not move across BOTH
            // intervals. The generation must not be older than the envelope it is paired with — a base
            // behind the refreshed state makes every later probe find the very edit the envelope
            // describes and answer Stale forever — and it must not be newer either, which would hide a
            // real edit from the probe. Reading it between the two state reads gives both, because the
            // confirmation covers the whole span (#2430).
            var snapshot = await _applier
                .CaptureStateTokenAsync(
                    conflict.ServiceId,
                    conflict.LayerId,
                    storageLayerId,
                    conflict.ObjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            var generation = await _changeTracker.GetCurrentGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
            var confirmation = await _applier
                .CaptureStateTokenAsync(
                    conflict.ServiceId,
                    conflict.LayerId,
                    storageLayerId,
                    conflict.ObjectId,
                    cancellationToken)
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
                        // A row that is gone is either a completed client delete (delete/delete) or a
                        // client update against a server deletion. Both classifications stop the record
                        // from owing a server envelope it can never have.
                        ConflictType: snapshot.Exists ? null : ClassifyAbsentRebaseline(conflict.ConflictType),
                        ClientStateJson: null,
                        ServerStateJson: snapshot.StateJson,
                        // The refreshed envelope IS the current server side, so the attribution flags
                        // that described the original upload no longer hold. Leaving ClientEditApplied
                        // true would let a later acceptClient take its no-op shortcut and report success
                        // while the row still held the server state.
                        ClientEditApplied: false,
                        ResolutionBaseGeneration: generation,
                        ClientEditOutcomeUnknown: false,
                        ClientEditSuperseded: false,
                        // Explicit, because null means "leave unchanged": a re-baseline onto a deleted
                        // row would otherwise keep advertising the old server envelope, and keepServer
                        // would report success against a state the feature no longer has.
                        ClearServerState: !snapshot.Exists),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ResolutionRebaselineFailed(_logger, conflict.ConflictId, ex);
        }
    }

    private static ReplicaConflictType ClassifyAbsentRebaseline(ReplicaConflictType conflictType)
        => conflictType is ReplicaConflictType.DeleteUpdate or ReplicaConflictType.DeleteDelete
            ? ReplicaConflictType.DeleteDelete
            : ReplicaConflictType.UpdateDelete;

    // An indeterminate write keeps its claim but back-dates the lease so a retry is not treated as a concurrent duplicate.
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
                expectedRowAbsent)
            {
                ReplaceAttributes = plan.ReplaceAttributes,
                ExplicitAttributeNames = plan.ExplicitAttributeNames
            },
            cancellationToken);

    // A late resolution must not overwrite an edit made after detection; records from before that generation stamp skip the check.
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

        var storageObjectId = conflict.StorageObjectId ?? conflict.ObjectId;
        var changes = await _changeTracker
            .GetChangesSinceAsync(baseGeneration, [storageLayerId], new HashSet<long> { storageObjectId }, cancellationToken)
            .ConfigureAwait(false);

        return changes.Any(change => change.ObjectId == storageObjectId);
    }

    // Releasing a failed write uses the post-claim record and a fresh cancellation token so detection updates made during the attempt are not erased.
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
        // codeql[cs/catch-of-all-exceptions]: release failure is logged so it cannot mask the feature-write error the caller is already reporting
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

internal readonly record struct ReplicaConflictResolutionServiceRequest(
    string ReplicaId,
    string ConflictId,
    ReplicaConflictResolutionAction Action,
    string ActionName,
    ReplicaConflictResolutionInputs Inputs,
    string Actor,
    string CorrelationId);
