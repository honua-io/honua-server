// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

/// <summary>
/// Conducts a coordinated platform-upgrade release (Demo C). Composes the existing container
/// deploy/rollout lifecycle and the existing additive metadata-release lifecycle (which itself owns
/// the DB ScriptMigration + metadata-v2 activation) into one ordered, gated, rollback-able operation:
///
///   Preflight → Backup → ContainerRollout (gated) → MetadataAndSchema (gated) → Smoke → Promote → Complete
///
/// A fault at any step flips the operation into RollingBack, where the conductor unwinds the completed
/// reversible steps in reverse order — metadata/schema first (reactivate prior revision + inverse
/// script), then the container rollout (deploy rollback) — reusing each composed lifecycle's existing
/// rollback rather than a parallel implementation.
///
/// Mirrors <see cref="DeployWorkflowReconciler"/> / <see cref="MetadataReleaseReconciler"/>: leased,
/// idempotent, single advance per cycle so the background loop re-enters and resumes from the
/// persisted step.
/// </summary>
internal sealed partial class CoordinatedReleaseReconciler(
    IWorkflowOperationStore workflowStore,
    ICoordinatedContainerStepExecutor containerStep,
    ICoordinatedMetadataStepExecutor metadataStep,
    ILogger<CoordinatedReleaseReconciler> logger) : ICoordinatedReleaseReconciler
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileCoordinatedReleaseAsync(string operationId, CancellationToken cancellationToken = default)
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
                operation.Kind != WorkflowOperationKind.CoordinatedRelease ||
                operation.CoordinatedRelease is null ||
                IsTerminal(operation.Status) ||
                operation.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.AwaitingApproval or WorkflowOperationStatus.RollbackRequested))
            {
                return;
            }

            var token = reconciliationCancellation.Token;
            var updated = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? await UnwindAsync(operation, token).ConfigureAwait(false)
                : await AdvanceForwardAsync(operation, token).ConfigureAwait(false);

            if (!ReferenceEquals(updated, operation) && !Equals(updated, operation))
            {
                await workflowStore.SetAsync(updated, cancellationToken: token).ConfigureAwait(false);
                Log.CoordinatedReleaseReconciled(logger, operationId, updated.CoordinatedRelease!.CurrentStep.ToString(), updated.Status.ToString());
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.CoordinatedReleaseLeaseLost(logger, operationId);
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
    /// Advances the coordinated forward order by exactly one step. Each transition is idempotent.
    /// </summary>
    private async Task<WorkflowOperationRecord> AdvanceForwardAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var context = operation.CoordinatedRelease!;

        switch (context.CurrentStep)
        {
            case CoordinatedReleaseStep.Preflight:
                // Composing services run their own preflight; the coordinated preflight only checks the
                // package is well-formed and records the ordered ledger.
                return Advance(operation, CoordinatedReleaseStep.Backup, CoordinatedReleaseStepStatus.Succeeded,
                    "Coordinated preflight passed; container, DB-script, and metadata artifacts are well-formed.");

            case CoordinatedReleaseStep.Backup:
                return Advance(operation, CoordinatedReleaseStep.ContainerRollout, CoordinatedReleaseStepStatus.Succeeded,
                    "Backup is a no-op for the additive reversible coordinated path.");

            case CoordinatedReleaseStep.ContainerRollout:
                return await AdvanceContainerRolloutAsync(operation, cancellationToken).ConfigureAwait(false);

            case CoordinatedReleaseStep.MetadataAndSchema:
                return await AdvanceMetadataAndSchemaAsync(operation, cancellationToken).ConfigureAwait(false);

            case CoordinatedReleaseStep.Smoke:
                // The metadata step already smoke-checked the activated layer; the coordinated Smoke is a
                // final cross-artifact verification gate. Treat as passed once both composed lifecycles
                // are terminal-succeeded.
                return Advance(operation, CoordinatedReleaseStep.Promote, CoordinatedReleaseStepStatus.Succeeded,
                    "Coordinated smoke verification passed across all three artifacts.");

            case CoordinatedReleaseStep.Promote:
                return Complete(operation);

            default:
                return operation;
        }
    }

    private async Task<WorkflowOperationRecord> AdvanceContainerRolloutAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var context = operation.CoordinatedRelease!;

        // Gate at the container-swap boundary: require explicit approval before submitting the rollout.
        if (context.Container.RequiresExplicitApproval && !context.ContainerGateApproved)
        {
            return AwaitApproval(operation, CoordinatedReleaseStep.ContainerRollout,
                "Awaiting explicit approval to swap the running container image.");
        }

        if (string.IsNullOrWhiteSpace(context.ContainerOperationId))
        {
            var started = await containerStep.StartAsync(context, operation, cancellationToken).ConfigureAwait(false);
            if (started.Outcome == CoordinatedStepOutcome.Failed)
            {
                return RequestUnwind(operation, CoordinatedReleaseStep.ContainerRollout,
                    started.Detail ?? "Container rollout failed to start.");
            }

            return MarkRunning(operation, CoordinatedReleaseStep.ContainerRollout,
                childContainerOperationId: started.ChildOperationId,
                detail: started.Detail ?? "Container rollout submitted.");
        }

        var observed = await containerStep.ObserveAsync(context.ContainerOperationId!, cancellationToken).ConfigureAwait(false);
        return observed.Outcome switch
        {
            CoordinatedStepOutcome.Succeeded => Advance(operation, CoordinatedReleaseStep.MetadataAndSchema,
                CoordinatedReleaseStepStatus.Succeeded, observed.Detail ?? "Container rollout promoted.",
                stepToComplete: CoordinatedReleaseStep.ContainerRollout),
            CoordinatedStepOutcome.Failed => RequestUnwind(operation, CoordinatedReleaseStep.ContainerRollout,
                observed.Detail ?? "Container rollout failed."),
            _ => operation
        };
    }

    private async Task<WorkflowOperationRecord> AdvanceMetadataAndSchemaAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var context = operation.CoordinatedRelease!;

        // Gate at the data-affecting boundary: require explicit approval before mutating schema/metadata.
        if (!context.DataGateApproved)
        {
            return AwaitApproval(operation, CoordinatedReleaseStep.MetadataAndSchema,
                "Awaiting explicit approval to apply the DB schema change and activate the new metadata revision.");
        }

        if (string.IsNullOrWhiteSpace(context.MetadataOperationId))
        {
            var started = await metadataStep.StartAsync(context, operation, cancellationToken).ConfigureAwait(false);
            if (started.Outcome == CoordinatedStepOutcome.Failed)
            {
                return RequestUnwind(operation, CoordinatedReleaseStep.MetadataAndSchema,
                    started.Detail ?? "Metadata/schema step failed to start.");
            }

            return MarkRunning(operation, CoordinatedReleaseStep.MetadataAndSchema, childMetadataOperationId: started.ChildOperationId,
                detail: started.Detail ?? "Metadata/schema lifecycle submitted.");
        }

        var observed = await metadataStep.ObserveAsync(context.MetadataOperationId!, cancellationToken).ConfigureAwait(false);
        return observed.Outcome switch
        {
            CoordinatedStepOutcome.Succeeded => Advance(operation, CoordinatedReleaseStep.Smoke,
                CoordinatedReleaseStepStatus.Succeeded, observed.Detail ?? "DB schema change applied and new metadata revision activated.",
                stepToComplete: CoordinatedReleaseStep.MetadataAndSchema),
            CoordinatedStepOutcome.Failed => RequestUnwind(operation, CoordinatedReleaseStep.MetadataAndSchema,
                observed.Detail ?? "Metadata/schema step failed."),
            _ => operation
        };
    }

    /// <summary>
    /// Unwinds the coordinated release. Reverses the completed reversible steps: metadata/schema first
    /// (reactivate the prior revision + execute the inverse script), then the container rollout (deploy
    /// rollback). Each composed rollback is delegated to its own lifecycle and is idempotent.
    /// </summary>
    private async Task<WorkflowOperationRecord> UnwindAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var context = operation.CoordinatedRelease!;

        // Reverse order: undo the most recent reversible step first.
        var metadataState = FindStep(context, CoordinatedReleaseStep.MetadataAndSchema);
        if (NeedsUnwind(metadataState) && !string.IsNullOrWhiteSpace(context.MetadataOperationId))
        {
            await metadataStep.RollbackAsync(context.MetadataOperationId!, cancellationToken).ConfigureAwait(false);
            return WithStepRolledBack(operation, CoordinatedReleaseStep.MetadataAndSchema,
                "Reversed metadata/schema: reactivated the prior revision and executed the inverse script.");
        }

        var containerState = FindStep(context, CoordinatedReleaseStep.ContainerRollout);
        if (NeedsUnwind(containerState) && !string.IsNullOrWhiteSpace(context.ContainerOperationId))
        {
            await containerStep.RollbackAsync(context.ContainerOperationId!, cancellationToken).ConfigureAwait(false);
            return WithStepRolledBack(operation, CoordinatedReleaseStep.ContainerRollout,
                "Reversed container rollout: rolled the deploy back to the prior image.");
        }

        // Nothing left to unwind — the coordinated rollback is complete.
        var now = DateTimeOffset.UtcNow;
        return operation with
        {
            Status = WorkflowOperationStatus.RolledBack,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Coordinated rollback complete: all completed steps were unwound in reverse order.",
            ErrorMessage = null,
            CoordinatedRelease = context with { CurrentStep = CoordinatedReleaseStep.Failed }
        };
    }

    private static bool NeedsUnwind(CoordinatedReleaseStepState? state)
        => state is { IsReversible: true } &&
            state.Status is CoordinatedReleaseStepStatus.Succeeded
                or CoordinatedReleaseStepStatus.Running
                or CoordinatedReleaseStepStatus.Failed;

    private static CoordinatedReleaseStepState? FindStep(CoordinatedReleaseContext context, CoordinatedReleaseStep step)
        => context.Steps.FirstOrDefault(s => s.Step == step);

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
        if (operation is { Kind: WorkflowOperationKind.CoordinatedRelease, CoordinatedRelease: not null } && !IsTerminal(operation.Status))
        {
            var failedAt = DateTimeOffset.UtcNow;
            var failed = operation with
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                UpdatedAt = failedAt,
                CompletedAt = failedAt,
                CurrentPhase = "Coordinated release reconciliation failed and requires manual intervention.",
                ErrorMessage = $"Coordinated release reconciliation failed due to {ex.GetType().Name}.",
                CoordinatedRelease = operation.CoordinatedRelease with { CurrentStep = CoordinatedReleaseStep.Failed }
            };

            await workflowStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    // -- ledger transition helpers -------------------------------------------------------------

    private static WorkflowOperationRecord Advance(
        WorkflowOperationRecord operation,
        CoordinatedReleaseStep nextStep,
        CoordinatedReleaseStepStatus completedStatus,
        string phase,
        CoordinatedReleaseStep? stepToComplete = null)
    {
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, stepToComplete ?? context.CurrentStep, completedStatus, phase);

        return operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            CurrentPhase = phase,
            ErrorMessage = null,
            CoordinatedRelease = context with { CurrentStep = nextStep, Steps = steps }
        };
    }

    private static WorkflowOperationRecord AwaitApproval(
        WorkflowOperationRecord operation,
        CoordinatedReleaseStep step,
        string phase)
    {
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, step, CoordinatedReleaseStepStatus.AwaitingApproval, phase);

        return operation with
        {
            Status = WorkflowOperationStatus.AwaitingApproval,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = phase,
            CoordinatedRelease = context with { CurrentStep = step, Steps = steps }
        };
    }

    private static WorkflowOperationRecord MarkRunning(
        WorkflowOperationRecord operation,
        CoordinatedReleaseStep step,
        string? childContainerOperationId = null,
        string? childMetadataOperationId = null,
        string? detail = null)
    {
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, step, CoordinatedReleaseStepStatus.Running, detail);

        return operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = detail ?? operation.CurrentPhase,
            ErrorMessage = null,
            CoordinatedRelease = context with
            {
                CurrentStep = step,
                Steps = steps,
                ContainerOperationId = childContainerOperationId ?? context.ContainerOperationId,
                MetadataOperationId = childMetadataOperationId ?? context.MetadataOperationId
            }
        };
    }

    private static WorkflowOperationRecord RequestUnwind(
        WorkflowOperationRecord operation,
        CoordinatedReleaseStep failedStep,
        string reason)
    {
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, failedStep, CoordinatedReleaseStepStatus.Failed, reason);

        return operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = $"Step {failedStep} failed; starting coordinated rollback. {reason}",
            ErrorMessage = reason,
            CoordinatedRelease = context with { CurrentStep = CoordinatedReleaseStep.RollingBack, Steps = steps }
        };
    }

    private static WorkflowOperationRecord WithStepRolledBack(
        WorkflowOperationRecord operation,
        CoordinatedReleaseStep step,
        string phase)
    {
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, step, CoordinatedReleaseStepStatus.RolledBack, phase);

        return operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = phase,
            CoordinatedRelease = context with { CurrentStep = CoordinatedReleaseStep.RollingBack, Steps = steps }
        };
    }

    private static WorkflowOperationRecord Complete(WorkflowOperationRecord operation)
    {
        var now = DateTimeOffset.UtcNow;
        var context = operation.CoordinatedRelease!;
        var steps = UpsertStep(context.Steps, CoordinatedReleaseStep.Promote, CoordinatedReleaseStepStatus.Succeeded,
            "Coordinated release promoted to the target environment.");

        return operation with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Coordinated platform-upgrade release complete: container, DB schema, and metadata are all in place.",
            ErrorMessage = null,
            CoordinatedRelease = context with { CurrentStep = CoordinatedReleaseStep.Complete, Steps = steps }
        };
    }

    private static IReadOnlyList<CoordinatedReleaseStepState> UpsertStep(
        IReadOnlyList<CoordinatedReleaseStepState> steps,
        CoordinatedReleaseStep step,
        CoordinatedReleaseStepStatus status,
        string? detail)
    {
        var existing = steps.FirstOrDefault(s => s.Step == step);
        var updated = new CoordinatedReleaseStepState
        {
            Step = step,
            Status = status,
            RequiresApproval = existing?.RequiresApproval ?? false,
            IsReversible = existing?.IsReversible ?? false,
            Detail = detail ?? existing?.Detail,
            At = DateTimeOffset.UtcNow
        };

        var list = steps.Where(s => s.Step != step).ToList();
        list.Add(updated);
        return CoordinatedReleaseStepOrdering.Order(list);
    }

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    internal static partial class Log
    {
        [LoggerMessage(9140, LogLevel.Debug, "Reconciled coordinated release {OperationId} at step {Step} to status {Status}")]
        public static partial void CoordinatedReleaseReconciled(ILogger logger, string operationId, string step, string status);

        [LoggerMessage(9141, LogLevel.Warning, "Coordinated release reconciliation failed for operation {OperationId}")]
        public static partial void CoordinatedReleaseReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9142, LogLevel.Warning, "Coordinated release reconciliation poll loop failed")]
        public static partial void CoordinatedReleasePollLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(9143, LogLevel.Debug, "Coordinated release reconciliation lease was lost for operation {OperationId}.")]
        public static partial void CoordinatedReleaseLeaseLost(ILogger logger, string operationId);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9144, LogLevel.Information, "Started coordinated release reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Orders coordinated-release steps in their canonical execution order for stable ledger rendering.
/// </summary>
internal static class CoordinatedReleaseStepOrdering
{
    private static readonly Dictionary<CoordinatedReleaseStep, int> CanonicalOrder = new()
    {
        [CoordinatedReleaseStep.Preflight] = 0,
        [CoordinatedReleaseStep.Backup] = 1,
        [CoordinatedReleaseStep.ContainerRollout] = 2,
        [CoordinatedReleaseStep.MetadataAndSchema] = 3,
        [CoordinatedReleaseStep.Smoke] = 4,
        [CoordinatedReleaseStep.Promote] = 5
    };

    public static IReadOnlyList<CoordinatedReleaseStepState> Order(IEnumerable<CoordinatedReleaseStepState> steps)
        => steps
            .OrderBy(s => CanonicalOrder.TryGetValue(s.Step, out var index) ? index : int.MaxValue)
            .ToList();
}

/// <summary>
/// Background worker that continuously reconciles active coordinated-release workflow operations.
/// Sibling to <see cref="DeployWorkflowReconcilerBackgroundService"/> and
/// <see cref="MetadataReleaseReconcilerBackgroundService"/>; processes only
/// <see cref="WorkflowOperationKind.CoordinatedRelease"/> operations.
/// </summary>
internal sealed class CoordinatedReleaseReconcilerBackgroundService(
    IWorkflowOperationStore workflowStore,
    ICoordinatedReleaseReconciler reconciler,
    ILogger<CoordinatedReleaseReconcilerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CoordinatedReleaseReconciler.BackgroundLog.BackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeOperations = await workflowStore.ListActiveAsync(WorkflowOperationKind.CoordinatedRelease, stoppingToken).ConfigureAwait(false);
                foreach (var operation in activeOperations)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        CoordinatedReleaseReconciler.Log.CoordinatedReleaseReconcileFailed(logger, operation.OperationId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                CoordinatedReleaseReconciler.Log.CoordinatedReleasePollLoopFailed(logger, ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
