// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

/// <summary>
/// Reconciles durable metadata-release workflow operations through the staged-activation lifecycle
/// for protected additive changes:
/// Preflight (change policy + compatibility gate, before any mutation) →
/// Backup (capture the immutable prior revision and its ETag) →
/// ScriptMigration (prepare and stage an immutable candidate; the active pointer does not move) →
/// ServicePublication (qualified additive ETL, still before activation) →
/// Smoke (exercise the candidate explicitly while canonical readers stay on the prior revision) →
/// MetadataApply (atomic, ETag-conditional activation; a newer update is rebased and revalidated or
/// rejected, never overwritten) →
/// SloWatch (post-activation regression check on the live revision) → Complete.
///
/// Rollback restores only what the operation owns — it reactivates the prior revision while the
/// candidate is still current, and otherwise reverts the owned fields on top of later updates — never
/// touches physical data, and verifies the recovered service before reporting RolledBack. Failures
/// before activation discard the candidate and leave the live catalog untouched. Snapshot-required
/// rollback is deferred and refused with a clear message.
///
/// Mirrors <see cref="DeployWorkflowReconciler"/>: leased, idempotent, single advance per cycle so
/// the background loop re-enters and resumes from the persisted stage.
/// </summary>
internal sealed partial class MetadataReleaseReconciler(
    IWorkflowOperationStore workflowStore,
    IMetadataReleasePreflightGate preflightGate,
    IMetadataReleaseScriptExecutor scriptExecutor,
    IMetadataReleaseDataJobDispatcher dataJobDispatcher,
    IMetadataReleaseActivator activator,
    IMetadataReleaseSmokeChecker smokeChecker,
    ILogger<MetadataReleaseReconciler> logger) : IMetadataReleaseOperationReconciler
{
    internal const int MaxRebaseAttempts = 3;
    private const int MaxRevertAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileMetadataReleaseAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var leaseAcquired = await workflowStore.TryAcquireLeaseAsync(operationId, _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewLeaseUntilCancelledAsync(operationId, reconciliationCancellation);

        try
        {
            var operation = await workflowStore.GetAsync(operationId, reconciliationCancellation.Token).ConfigureAwait(false);
            if (operation is null ||
                operation.Kind != WorkflowOperationKind.MetadataRelease ||
                operation.MetadataRelease is null ||
                IsTerminal(operation.Status) ||
                operation.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested))
            {
                return;
            }

            var token = reconciliationCancellation.Token;
            var updated = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? await ExecuteRollbackAsync(operation, token).ConfigureAwait(false)
                : await AdvanceForwardAsync(operation, token).ConfigureAwait(false);

            if (!ReferenceEquals(updated, operation) && !Equals(updated, operation))
            {
                await workflowStore.SetAsync(updated, cancellationToken: token).ConfigureAwait(false);
                Log.MetadataReleaseReconciled(logger, operationId, updated.MetadataRelease!.CurrentStage.ToString(), updated.Status.ToString());
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.MetadataReleaseLeaseLost(logger, operationId);
            return;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(operationId, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            reconciliationCancellation.Cancel();
            try
            {
                await renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
                // Expected: the Cancel() call above is what stops the lease-renewal loop,
                // so its resulting OperationCanceledException is normal shutdown, not an error.
            }

            await workflowStore.ReleaseLeaseAsync(operationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Advances the forward lifecycle by exactly one stage so the leased loop re-enters and resumes
    /// deterministically. Every stage is idempotent against the persisted record: a crash between a
    /// side effect and the record write re-runs the stage and observes the side effect instead of
    /// repeating it.
    /// </summary>
    private async Task<WorkflowOperationRecord> AdvanceForwardAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        var plan = release.ExecutionPlan;
        if (plan is null)
        {
            return Fail(operation, "Metadata release has no executable additive plan; the additive lifecycle cannot proceed.");
        }

        switch (release.CurrentStage)
        {
            case MetadataReleaseStage.Preflight:
                {
                    // Destructive changes and ETL whose compensation is unproven are rejected before
                    // the first write, independent of the package analyzer's classification.
                    var policy = MetadataReleaseChangePolicy.Evaluate(plan);
                    if (!policy.IsAllowed)
                    {
                        return Block(operation, $"Change policy rejected the release before any mutation: {string.Join(" ", policy.Reasons)}", policy.Blockers);
                    }

                    var preflight = await preflightGate.EvaluateAsync(plan, cancellationToken).ConfigureAwait(false);
                    if (!preflight.CanProceed)
                    {
                        return Block(operation, $"Preflight gate blocked the release: {preflight.Reason}", preflight.Blockers);
                    }

                    // Safe-by-default: refuse to auto-execute a release that the gate classifies as
                    // requiring a snapshot restore for rollback. Snapshot rollback is deferred.
                    if (preflight.RollbackClassification == MetadataRollbackReadinessClassification.SnapshotRequired)
                    {
                        return Block(
                            operation,
                            "Preflight classified rollback as snapshot-required; snapshot-based metadata release is not yet implemented. " +
                            "Only additive reversible (script-reversible / metadata-only) releases are supported on this path.",
                            ["metadata-release-snapshot-not-implemented"]);
                    }

                    return Advance(
                        operation,
                        MetadataReleaseStage.Backup,
                        $"Preflight passed; rollback classified as {preflight.RollbackClassification}.",
                        release with { RollbackPlan = BuildRollbackPlan(preflight.RollbackClassification) });
                }

            case MetadataReleaseStage.Backup:
                return await CapturePriorAsync(operation, cancellationToken).ConfigureAwait(false);

            case MetadataReleaseStage.ScriptMigration:
                return await PrepareCandidateAsync(operation, plan, cancellationToken).ConfigureAwait(false);

            case MetadataReleaseStage.ServicePublication:
                {
                    // Qualified additive ETL runs before activation: it only writes the new nullable
                    // fields (enforced at Preflight), which the active revision does not expose.
                    bool dispatched;
                    try
                    {
                        dispatched = await dataJobDispatcher.DispatchAndAwaitAsync(plan, operation.OperationId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return await AbandonCandidateAsync(
                            operation,
                            $"Data-populate job failed before activation ({ex.GetType().Name}).",
                            "metadata-release-etl-failed",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return Advance(
                        operation,
                        MetadataReleaseStage.Smoke,
                        dispatched
                            ? $"Data-populate job '{plan.DataPopulateWorkloadId}' completed before activation."
                            : "No data-populate workload declared; skipping ETL populate.");
                }

            case MetadataReleaseStage.Smoke:
                {
                    var smoke = await smokeChecker.RunAsync(
                            plan,
                            new MetadataReleaseSmokeRequest
                            {
                                Phase = MetadataReleaseSmokePhase.Candidate,
                                Revision = release.CandidateRevision!.Value,
                                BaselineRevision = release.PriorRevision,
                                ExpectNewField = true
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    var withEvidence = WithEvidence(operation, "smoke-candidate");
                    if (!smoke.Passed)
                    {
                        return await AbandonCandidateAsync(
                            withEvidence,
                            $"Candidate smoke check failed: {smoke.Message}",
                            "metadata-release-candidate-smoke-failed",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return Advance(
                        withEvidence,
                        MetadataReleaseStage.MetadataApply,
                        $"Candidate revision {release.CandidateRevision} passed smoke before activation: {smoke.Message}");
                }

            case MetadataReleaseStage.MetadataApply:
                return await ActivateCandidateAsync(operation, cancellationToken).ConfigureAwait(false);

            case MetadataReleaseStage.SloWatch:
                {
                    var live = await activator.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                    var smoke = await smokeChecker.RunAsync(
                            plan,
                            new MetadataReleaseSmokeRequest
                            {
                                Phase = MetadataReleaseSmokePhase.Activated,
                                Revision = live.Revision,
                                BaselineRevision = release.PriorRevision,
                                ExpectNewField = true
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    var withEvidence = WithEvidence(operation, "smoke");
                    if (smoke.Passed)
                    {
                        return Complete(withEvidence, $"Smoke check passed on live revision {live.Revision}: {smoke.Message}");
                    }

                    // Post-activation regression: hand off to the verified rollback on the next cycle.
                    Log.MetadataReleaseSmokeFailed(logger, operation.OperationId, smoke.Message);
                    return RequestRollback(withEvidence, $"Smoke check failed after activation of revision {live.Revision}: {smoke.Message}");
                }

            default:
                return operation;
        }
    }

    /// <summary>
    /// Captures the active revision and its ETag before any mutation. Only a retained revision can
    /// serve as the immutable base: a synthesized compatibility or empty graph is refused.
    /// </summary>
    private async Task<WorkflowOperationRecord> CapturePriorAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        if (release.PriorRevision.HasValue && release.PriorEtag is not null)
        {
            return Advance(operation, MetadataReleaseStage.ScriptMigration, $"Prior revision {release.PriorRevision} already captured; resuming.");
        }

        var current = await activator.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var retained = await activator.GetRevisionAsync(current.Revision, cancellationToken).ConfigureAwait(false);
        if (retained is null || !string.Equals(retained.Etag, current.Etag, StringComparison.Ordinal))
        {
            return Block(
                operation,
                $"The active metadata graph (revision {current.Revision}) is not a retained Metadata v2 revision, so its state cannot be captured immutably. " +
                "Publish a Metadata v2 revision before running a protected release.",
                ["metadata-release-prior-not-retained"]);
        }

        return Advance(
            operation,
            MetadataReleaseStage.ScriptMigration,
            $"Captured prior revision {current.Revision} and its ETag before any mutation.",
            release with { PriorRevision = current.Revision, PriorEtag = current.Etag });
    }

    /// <summary>
    /// Prepares the candidate from the immutable prior revision, validates it and stages it as a
    /// retained revision. The active pointer does not move, so canonical readers keep the prior revision.
    /// </summary>
    private async Task<WorkflowOperationRecord> PrepareCandidateAsync(
        WorkflowOperationRecord operation,
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        if (release.CandidateRevision is long staged &&
            await activator.GetRevisionAsync(staged, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Advance(operation, MetadataReleaseStage.ServicePublication, $"Candidate revision {staged} already staged; resuming.");
        }

        var prior = release.PriorRevision is long priorRevision
            ? await activator.GetRevisionAsync(priorRevision, cancellationToken).ConfigureAwait(false)
            : null;
        if (prior is null)
        {
            return Block(
                operation,
                $"Prior revision {release.PriorRevision} is no longer retained; the candidate cannot be prepared from immutable state.",
                ["metadata-release-prior-not-retained"]);
        }

        MetadataReleaseScriptResult prepared;
        MetadataV2GraphSnapshot candidate;
        try
        {
            prepared = scriptExecutor.PrepareForward(plan, prior.Graph);
            var validation = MetadataV2GraphValidator.Validate(prepared.Graph);
            if (!validation.IsValid)
            {
                throw new MetadataReleasePreparationException(
                    "metadata-release-candidate-invalid",
                    $"The candidate graph failed validation: {string.Join("; ", validation.Errors)}");
            }

            candidate = await activator.StageAsync(prepared.Graph, cancellationToken).ConfigureAwait(false);
        }
        catch (MetadataReleasePreparationException ex)
        {
            return Block(
                operation,
                $"Candidate preparation failed; the live catalog is unchanged at revision {prior.Revision}: {ex.Message}",
                [ex.Code]);
        }

        return Advance(
            operation,
            MetadataReleaseStage.ServicePublication,
            $"Staged candidate revision {candidate.Revision} from prior revision {prior.Revision}; canonical readers remain on revision {prior.Revision}.",
            release with
            {
                CandidateRevision = candidate.Revision,
                CandidateEtag = candidate.Etag,
                OwnedOperations = prepared.AppliedOperations
            });
    }

    /// <summary>
    /// Moves the current pointer to the candidate only if the prior revision is still current. A
    /// newer update is never overwritten: the change is rebased onto it and revalidated, or rejected
    /// after <see cref="MaxRebaseAttempts"/> rebases.
    /// </summary>
    private async Task<WorkflowOperationRecord> ActivateCandidateAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        var candidateRevision = release.CandidateRevision!.Value;
        var result = await activator.ActivateAsync(candidateRevision, release.PriorEtag!, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != MetadataReleaseActivationOutcome.Conflict)
        {
            return Advance(
                operation,
                MetadataReleaseStage.SloWatch,
                $"Activated candidate revision {candidateRevision} atomically over prior revision {release.PriorRevision}.",
                release with { ActivatedAt = release.ActivatedAt ?? DateTimeOffset.UtcNow });
        }

        Log.MetadataReleaseActivationConflict(logger, operation.OperationId, release.PriorRevision ?? 0, result.CurrentRevision);
        if (release.RebaseCount >= MaxRebaseAttempts)
        {
            return await AbandonCandidateAsync(
                operation,
                $"Activation rejected: revision {result.CurrentRevision} became current and the change was already rebased {release.RebaseCount} time(s); nothing was overwritten.",
                "metadata-release-activation-conflict",
                cancellationToken).ConfigureAwait(false);
        }

        await activator.DiscardAsync(candidateRevision, cancellationToken).ConfigureAwait(false);
        return Advance(
            operation,
            MetadataReleaseStage.ScriptMigration,
            $"Revision {result.CurrentRevision} became current before activation; rebasing the change onto it and revalidating. Nothing was overwritten.",
            release with
            {
                PriorRevision = result.CurrentRevision,
                PriorEtag = result.CurrentEtag,
                CandidateRevision = null,
                CandidateEtag = null,
                OwnedOperations = Array.Empty<MetadataReleaseScriptOperation>(),
                RebaseCount = release.RebaseCount + 1,
                Warnings = [.. release.Warnings, $"Rebased from revision {release.PriorRevision} onto concurrent revision {result.CurrentRevision} before activation."]
            });
    }

    /// <summary>
    /// Ends a release that failed before activation: discards the staged candidate (canonical readers
    /// never left the prior revision) and fails the operation with a stable blocker.
    /// </summary>
    private async Task<WorkflowOperationRecord> AbandonCandidateAsync(
        WorkflowOperationRecord operation,
        string reason,
        string blocker,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        if (release.CandidateRevision is long candidateRevision)
        {
            await activator.DiscardAsync(candidateRevision, cancellationToken).ConfigureAwait(false);
        }

        return Block(
            operation,
            $"{reason} The candidate was discarded; the live catalog is unchanged at revision {release.PriorRevision}.",
            [blocker]);
    }

    /// <summary>
    /// Executes the requested rollback: restores only the operation-owned change, never touches
    /// physical data or unrelated updates, and verifies the recovered service before reporting
    /// RolledBack. Snapshot-required is refused — that path is deferred.
    /// </summary>
    private async Task<WorkflowOperationRecord> ExecuteRollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var release = operation.MetadataRelease!;
        var plan = release.ExecutionPlan;
        var rollbackClass = release.RollbackPlan?.Class ?? MetadataRollbackClass.MetadataOnly;

        if (rollbackClass == MetadataRollbackClass.SnapshotRestore)
        {
            return Block(
                operation,
                "Snapshot rollback is not yet implemented; this release requires a database snapshot/restore which is deferred. " +
                "Operator-managed recovery is required.",
                ["metadata-release-snapshot-rollback-not-implemented"],
                manualIntervention: true);
        }

        if (plan is null)
        {
            return Block(
                operation,
                "Rollback requested but the release carries no executable plan; manual recovery is required.",
                ["metadata-release-rollback-no-plan"],
                manualIntervention: true);
        }

        var restored = await RestorePriorRevisionAsync(release, cancellationToken).ConfigureAwait(false);
        var outcome = restored ?? await RevertOwnedChangesAsync(release, cancellationToken).ConfigureAwait(false);
        if (outcome.Blocker is not null)
        {
            return Block(operation, outcome.Message, [outcome.Blocker], manualIntervention: true);
        }

        var live = await activator.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (release.ActivatedAt is null && release.CandidateRevision is long candidateRevision && live.Revision != candidateRevision)
        {
            // Never activated: the staged candidate was only ever visible to the smoke check.
            await activator.DiscardAsync(candidateRevision, cancellationToken).ConfigureAwait(false);
        }

        // Verify recovered functional behavior before reporting RolledBack: the owned field is gone
        // (or, when the release owned nothing, the prior schema is intact), bindings and authorization
        // match the prior revision, and the canonical query still returns the preserved rows.
        var verification = await smokeChecker.RunAsync(
                plan,
                new MetadataReleaseSmokeRequest
                {
                    Phase = MetadataReleaseSmokePhase.Recovered,
                    Revision = live.Revision,
                    BaselineRevision = release.PriorRevision,
                    ExpectNewField = await ExpectNewFieldAfterRecoveryAsync(release, plan, live, cancellationToken).ConfigureAwait(false)
                },
                cancellationToken)
            .ConfigureAwait(false);
        var withEvidence = WithEvidence(operation, "smoke-recovered");
        if (!verification.Passed)
        {
            return Block(
                withEvidence,
                $"Rollback restored the metadata ({outcome.Message}) but the recovered service failed verification: {verification.Message}",
                ["metadata-release-recovery-unverified"],
                manualIntervention: true);
        }

        var now = DateTimeOffset.UtcNow;
        return withEvidence with
        {
            Status = WorkflowOperationStatus.RolledBack,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = $"Reversible rollback complete ({rollbackClass}): {outcome.Message} Recovered revision {live.Revision} verified: {verification.Message}",
            ErrorMessage = null,
            MetadataRelease = withEvidence.MetadataRelease! with { CurrentStage = MetadataReleaseStage.RollbackRequested }
        };
    }

    /// <summary>
    /// While the candidate is still current, the prior revision plus the owned change is exactly the
    /// live state, so reactivating the prior revision restores precisely the operation-owned change.
    /// Returns null when that exact restore does not apply.
    /// </summary>
    private async Task<RecoveryOutcome?> RestorePriorRevisionAsync(
        MetadataReleaseContext release,
        CancellationToken cancellationToken)
    {
        if (release.PriorRevision is not long priorRevision || release.CandidateRevision is not long candidateRevision)
        {
            return null;
        }

        var live = await activator.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (live.Revision != candidateRevision ||
            await activator.GetRevisionAsync(priorRevision, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var result = await activator.ActivateAsync(priorRevision, live.Etag, cancellationToken).ConfigureAwait(false);
        return result.Outcome == MetadataReleaseActivationOutcome.Conflict
            ? null
            : new RecoveryOutcome($"Reactivated prior revision {priorRevision} while candidate revision {candidateRevision} was still current.");
    }

    /// <summary>
    /// Reverts only the owned field changes on top of whatever is live now, preserving every later
    /// update, with an ETag-conditional commit retried against concurrent writers.
    /// </summary>
    private async Task<RecoveryOutcome> RevertOwnedChangesAsync(
        MetadataReleaseContext release,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRevertAttempts; attempt++)
        {
            var live = await activator.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            MetadataReleaseScriptResult inverse;
            try
            {
                inverse = scriptExecutor.PrepareInverse(release.OwnedOperations, live.Graph);
            }
            catch (MetadataReleasePreparationException ex)
            {
                return new RecoveryOutcome($"Rollback cannot revert only this release's change: {ex.Message}", ex.Code);
            }

            if (inverse.AppliedOperations.Count == 0)
            {
                return new RecoveryOutcome($"No change owned by this release is live at revision {live.Revision}; nothing to revert.");
            }

            try
            {
                var reverted = await activator.CommitRevertAsync(inverse.Graph, live.Etag, cancellationToken).ConfigureAwait(false);
                return new RecoveryOutcome(
                    $"Reverted {inverse.AppliedOperations.Count} owned field change(s) on top of revision {live.Revision} as revision {reverted.Revision}, preserving later updates.");
            }
            catch (MetadataV2GraphConcurrencyException)
            {
                // Another writer advanced the live revision; re-read and revert on top of it.
            }
        }

        return new RecoveryOutcome(
            $"Concurrent updates superseded the revert {MaxRevertAttempts} times; operator-managed recovery is required.",
            "metadata-release-rollback-conflict");
    }

    private async Task<bool> ExpectNewFieldAfterRecoveryAsync(
        MetadataReleaseContext release,
        MetadataReleaseExecutionPlan plan,
        MetadataV2GraphSnapshot live,
        CancellationToken cancellationToken)
    {
        var owned = release.OwnedOperations.Any(operation =>
            string.Equals(operation.ResourceSemanticId, plan.ResourceSemanticId, StringComparison.Ordinal) &&
            string.Equals(operation.FieldName, plan.NewFieldName, StringComparison.OrdinalIgnoreCase));
        if (owned)
        {
            return false;
        }

        // The release did not add the field: recovery must preserve whatever the prior revision had.
        var reference = release.PriorRevision is long priorRevision
            ? await activator.GetRevisionAsync(priorRevision, cancellationToken).ConfigureAwait(false) ?? live
            : live;
        return reference.Index.ResourcesById.TryGetValue(plan.ResourceSemanticId, out var resource) &&
            resource.SchemaFields.Any(field => string.Equals(field.Name, plan.NewFieldName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RenewLeaseUntilCancelledAsync(string operationId, CancellationTokenSource reconciliationCancellation)
    {
        while (!reconciliationCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewInterval, reconciliationCancellation.Token).ConfigureAwait(false);
                var renewed = await workflowStore.RenewLeaseAsync(operationId, _ownerId, LeaseDuration, reconciliationCancellation.Token).ConfigureAwait(false);
                if (!renewed)
                {
                    reconciliationCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task MarkFailedAsync(string operationId, Exception ex, CancellationToken cancellationToken)
    {
        var operation = await workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is { Kind: WorkflowOperationKind.MetadataRelease, MetadataRelease: not null } && !IsTerminal(operation.Status))
        {
            var failedAt = DateTimeOffset.UtcNow;
            var failed = operation with
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                UpdatedAt = failedAt,
                CompletedAt = failedAt,
                CurrentPhase = "Metadata release reconciliation failed and requires manual intervention.",
                ErrorMessage = $"Metadata release reconciliation failed due to {ex.GetType().Name}.",
                MetadataRelease = operation.MetadataRelease with { CurrentStage = MetadataReleaseStage.Failed }
            };

            await workflowStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static MetadataRollbackPlan BuildRollbackPlan(MetadataRollbackReadinessClassification classification)
    {
        var rollbackClass = classification switch
        {
            MetadataRollbackReadinessClassification.ScriptReversible => MetadataRollbackClass.ScriptRollback,
            MetadataRollbackReadinessClassification.ServiceRevision => MetadataRollbackClass.ServiceRevisionRevert,
            MetadataRollbackReadinessClassification.SnapshotRequired => MetadataRollbackClass.SnapshotRestore,
            MetadataRollbackReadinessClassification.Manual => MetadataRollbackClass.ManualRecovery,
            _ => MetadataRollbackClass.MetadataOnly
        };

        return new MetadataRollbackPlan
        {
            Class = rollbackClass,
            Steps =
            [
                "Reactivate the prior revision while the candidate is still current; otherwise revert only this release's owned fields on top of later updates.",
                "Leave physical data and committed feature edits untouched.",
                "Verify the recovered revision (schema, bindings, authorization, canonical query) before reporting RolledBack."
            ]
        };
    }

    private static WorkflowOperationRecord WithEvidence(WorkflowOperationRecord operation, string kind)
        => operation with
        {
            MetadataRelease = operation.MetadataRelease! with
            {
                EvidenceRefs =
                [
                    .. operation.MetadataRelease!.EvidenceRefs,
                    new MetadataEvidenceRef { Kind = kind, RefId = $"{kind}:{operation.OperationId}", At = DateTimeOffset.UtcNow }
                ]
            }
        };

    private static WorkflowOperationRecord Advance(
        WorkflowOperationRecord operation,
        MetadataReleaseStage nextStage,
        string phase,
        MetadataReleaseContext? release = null)
        => operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            CurrentPhase = phase,
            ErrorMessage = null,
            MetadataRelease = (release ?? operation.MetadataRelease!) with { CurrentStage = nextStage }
        };

    private static WorkflowOperationRecord Complete(WorkflowOperationRecord operation, string phase)
    {
        var now = DateTimeOffset.UtcNow;
        return operation with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = phase,
            ErrorMessage = null,
            MetadataRelease = operation.MetadataRelease! with { CurrentStage = MetadataReleaseStage.Complete }
        };
    }

    private static WorkflowOperationRecord RequestRollback(WorkflowOperationRecord operation, string phase)
        => operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            CurrentPhase = phase,
            ErrorMessage = phase,
            MetadataRelease = operation.MetadataRelease! with { CurrentStage = MetadataReleaseStage.RollbackRequested }
        };

    private static WorkflowOperationRecord Fail(WorkflowOperationRecord operation, string message)
    {
        var now = DateTimeOffset.UtcNow;
        return operation with
        {
            Status = WorkflowOperationStatus.Failed,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = message,
            ErrorMessage = message,
            MetadataRelease = operation.MetadataRelease! with { CurrentStage = MetadataReleaseStage.Failed }
        };
    }

    private static WorkflowOperationRecord Block(
        WorkflowOperationRecord operation,
        string message,
        IReadOnlyList<string> blockers,
        bool manualIntervention = false)
    {
        var now = DateTimeOffset.UtcNow;
        return operation with
        {
            Status = manualIntervention ? WorkflowOperationStatus.ManualInterventionRequired : WorkflowOperationStatus.Failed,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = message,
            ErrorMessage = message,
            BlockingReasons = blockers,
            MetadataRelease = operation.MetadataRelease! with
            {
                CurrentStage = MetadataReleaseStage.Failed,
                Blockers = blockers
            }
        };
    }

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private sealed record RecoveryOutcome(string Message, string? Blocker = null);

    internal static partial class Log
    {
        [LoggerMessage(9120, LogLevel.Debug, "Reconciled metadata release operation {OperationId} at stage {Stage} to status {Status}")]
        public static partial void MetadataReleaseReconciled(ILogger logger, string operationId, string stage, string status);

        [LoggerMessage(9121, LogLevel.Warning, "Metadata release reconciliation failed for operation {OperationId}")]
        public static partial void MetadataReleaseReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9122, LogLevel.Warning, "Metadata release reconciliation poll loop failed")]
        public static partial void MetadataReleasePollLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(9123, LogLevel.Debug, "Metadata release reconciliation lease was lost for operation {OperationId}; another node may continue processing.")]
        public static partial void MetadataReleaseLeaseLost(ILogger logger, string operationId);

        [LoggerMessage(9124, LogLevel.Warning, "Metadata release smoke check failed for operation {OperationId}: {Message}")]
        public static partial void MetadataReleaseSmokeFailed(ILogger logger, string operationId, string message);

        [LoggerMessage(9126, LogLevel.Warning, "Metadata release {OperationId} lost activation to a concurrent update: expected prior revision {PriorRevision}, found revision {CurrentRevision}")]
        public static partial void MetadataReleaseActivationConflict(ILogger logger, string operationId, long priorRevision, long currentRevision);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9125, LogLevel.Information, "Started metadata release reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Background worker that continuously reconciles active metadata-release workflow operations.
/// Sibling to <see cref="DeployWorkflowReconcilerBackgroundService"/>; processes only
/// <see cref="WorkflowOperationKind.MetadataRelease"/> operations.
/// </summary>
internal sealed class MetadataReleaseReconcilerBackgroundService(
    IWorkflowOperationStore workflowStore,
    IMetadataReleaseOperationReconciler reconciler,
    ILogger<MetadataReleaseReconcilerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MetadataReleaseReconciler.BackgroundLog.BackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeOperations = await workflowStore.ListActiveAsync(WorkflowOperationKind.MetadataRelease, stoppingToken).ConfigureAwait(false);
                foreach (var operation in activeOperations)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileMetadataReleaseAsync(operation.OperationId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Intentional broad catch: per-item loop over active operations; one
                        // operation's reconcile failure must not abort the rest of the batch.
                        MetadataReleaseReconciler.Log.MetadataReleaseReconcileFailed(logger, operation.OperationId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentionally generic: this is a long-running background polling loop.
                // A single failed iteration must not kill the host's background service;
                // log and keep polling.
                MetadataReleaseReconciler.Log.MetadataReleasePollLoopFailed(logger, ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
