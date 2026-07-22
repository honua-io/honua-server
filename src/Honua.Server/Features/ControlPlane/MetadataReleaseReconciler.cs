// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

/// <summary>
/// Reconciles durable metadata-release workflow operations through the additive layer-evolution
/// lifecycle. Walks the stages for the additive case — Preflight (compatibility/sync-check gate) →
/// Backup (no-op for additive) → ScriptMigration (additive schema change) → data/ETL populate →
/// MetadataApply (activate the new revision) → Smoke → Complete — and executes the reversible
/// inverse on rollback. Snapshot-required rollback is deferred and refused with a clear message.
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
    /// Advances the additive forward lifecycle by exactly one stage so the leased loop re-enters and
    /// resumes deterministically. Each stage transition is idempotent: re-running a stage on the same
    /// record produces the same next state.
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

                    var rollbackPlan = BuildRollbackPlan(preflight.RollbackClassification, plan);
                    return Advance(operation, MetadataReleaseStage.Backup, "Preflight passed; rollback classified as "
                        + $"{preflight.RollbackClassification}.", rollbackPlan: rollbackPlan);
                }

            case MetadataReleaseStage.Backup:
                // Additive evolution needs no data backup — only the snapshot-required class does, and
                // that class is refused at Preflight. Record the no-op and advance.
                return Advance(operation, MetadataReleaseStage.ScriptMigration, "Backup is a no-op for additive layer evolution.");

            case MetadataReleaseStage.ScriptMigration:
                await scriptExecutor.ApplyForwardAsync(plan, cancellationToken).ConfigureAwait(false);
                return Advance(operation, MetadataReleaseStage.ServicePublication, $"Applied additive schema script '{plan.Script.ScriptId}'.");

            case MetadataReleaseStage.ServicePublication:
                {
                    // Dispatch the optional ETL/data-populate workload as a control-plane job before the
                    // new revision is activated so queries observe populated data at Smoke.
                    var dispatched = await dataJobDispatcher.DispatchAndAwaitAsync(plan, operation.OperationId, cancellationToken).ConfigureAwait(false);
                    var message = dispatched
                        ? $"Data-populate job '{plan.DataPopulateWorkloadId}' completed."
                        : "No data-populate workload declared; skipping ETL populate.";
                    return Advance(operation, MetadataReleaseStage.MetadataApply, message);
                }

            case MetadataReleaseStage.MetadataApply:
                {
                    // Capture the prior revision before activation so a reversible rollback can reactivate
                    // it, then activate the new revision through the canonical graph store.
                    var priorRevision = await activator.GetCurrentRevisionAsync(plan, cancellationToken).ConfigureAwait(false);
                    await activator.ActivateNewRevisionAsync(plan, cancellationToken).ConfigureAwait(false);
                    return Advance(
                        operation,
                        MetadataReleaseStage.Smoke,
                        "Activated the new Metadata v2 revision.",
                        priorRevision: priorRevision);
                }

            case MetadataReleaseStage.Smoke:
                {
                    var smoke = await smokeChecker.RunAsync(plan, cancellationToken).ConfigureAwait(false);
                    var evidence = new MetadataEvidenceRef
                    {
                        Kind = "smoke",
                        RefId = $"smoke:{operation.OperationId}",
                        At = DateTimeOffset.UtcNow
                    };

                    if (smoke.Passed)
                    {
                        return Complete(operation, $"Smoke check passed: {smoke.Message}", evidence);
                    }

                    // Smoke failure triggers rollback: hand off to the rollback path on the next cycle.
                    Log.MetadataReleaseSmokeFailed(logger, operation.OperationId, smoke.Message);
                    return RequestRollback(operation, $"Smoke check failed: {smoke.Message}", evidence);
                }

            default:
                return operation;
        }
    }

    /// <summary>
    /// Executes the requested rollback. Reversible classes (script-reversible / metadata-only)
    /// reactivate the prior revision and run the reversible inverse script. Snapshot-required is
    /// refused — that path is deferred.
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

        // Reactivate the prior revision first so readers fall back to the known-good schema, then
        // execute the reversible inverse (drop the added column). Both steps are idempotent.
        if (release.PriorRevision.HasValue)
        {
            await activator.ReactivateRevisionAsync(plan, release.PriorRevision.Value, cancellationToken).ConfigureAwait(false);
        }

        await scriptExecutor.ApplyInverseAsync(plan, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        return operation with
        {
            Status = WorkflowOperationStatus.RolledBack,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = $"Reversible rollback complete ({rollbackClass}): reactivated prior revision and executed the inverse script.",
            ErrorMessage = null,
            MetadataRelease = release with { CurrentStage = MetadataReleaseStage.RollbackRequested }
        };
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

    private static MetadataRollbackPlan BuildRollbackPlan(
        MetadataRollbackReadinessClassification classification,
        MetadataReleaseExecutionPlan plan)
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
            Steps = [$"Reactivate the prior revision and execute the reversible inverse of script '{plan.Script.ScriptId}'."]
        };
    }

    private static WorkflowOperationRecord Advance(
        WorkflowOperationRecord operation,
        MetadataReleaseStage nextStage,
        string phase,
        MetadataRollbackPlan? rollbackPlan = null,
        long? priorRevision = null)
    {
        var release = operation.MetadataRelease! with
        {
            CurrentStage = nextStage,
            RollbackPlan = rollbackPlan ?? operation.MetadataRelease!.RollbackPlan,
            PriorRevision = priorRevision ?? operation.MetadataRelease!.PriorRevision
        };

        return operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            CurrentPhase = phase,
            ErrorMessage = null,
            MetadataRelease = release
        };
    }

    private static WorkflowOperationRecord Complete(
        WorkflowOperationRecord operation,
        string phase,
        MetadataEvidenceRef evidence)
    {
        var now = DateTimeOffset.UtcNow;
        var release = operation.MetadataRelease! with
        {
            CurrentStage = MetadataReleaseStage.Complete,
            EvidenceRefs = [.. operation.MetadataRelease!.EvidenceRefs, evidence]
        };

        return operation with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = phase,
            ErrorMessage = null,
            MetadataRelease = release
        };
    }

    private static WorkflowOperationRecord RequestRollback(
        WorkflowOperationRecord operation,
        string phase,
        MetadataEvidenceRef evidence)
    {
        var release = operation.MetadataRelease! with
        {
            CurrentStage = MetadataReleaseStage.RollbackRequested,
            EvidenceRefs = [.. operation.MetadataRelease!.EvidenceRefs, evidence]
        };

        return operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            CurrentPhase = phase,
            ErrorMessage = phase,
            MetadataRelease = release
        };
    }

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
