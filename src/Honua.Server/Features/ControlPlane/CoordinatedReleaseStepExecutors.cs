// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Composes the existing container deploy/rollout lifecycle as a coordinated-release step. Thin
/// adapter over <see cref="DeployWorkflowService"/>: it submits a deploy operation for the rollout,
/// observes it through the deploy reconciler, and rolls it back through the deploy rollback path. It
/// does not reimplement the rollout, canary, or backend dispatch behavior.
/// </summary>
internal sealed class CoordinatedContainerStepExecutor(
    DeployWorkflowService deployService,
    IWorkflowOperationReconciler deployReconciler,
    IEnumerable<IWorkflowOperationStore> workflowStores) : ICoordinatedContainerStepExecutor
{
    private readonly IWorkflowOperationStore? _workflowStore = workflowStores.FirstOrDefault();

    public async Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        var deploy = await deployService.CreateAsync(
                context.Container.TargetId,
                context.Container.DesiredImage,
                context.Container.CurrentImage,
                operation.Audit.RequestedBy,
                $"Coordinated release {operation.OperationId} container rollout.",
                idempotencyKey: $"{operation.OperationId}-container",
                correlationId: operation.Audit.CorrelationId,
                priority: OperationPriority.Normal,
                submitImmediately: true,
                parameterOverrides: null,
                principal: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            return new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Failed,
                Detail = $"No deploy target '{context.Container.TargetId}' is registered for the container rollout."
            };
        }

        return new CoordinatedStepResult
        {
            Outcome = MapOutcome(deploy.Status),
            ChildOperationId = deploy.OperationId,
            Detail = deploy.CurrentPhase ?? "Container rollout submitted."
        };
    }

    public async Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null)
        {
            return new CoordinatedStepResult { Outcome = CoordinatedStepOutcome.Pending };
        }

        await deployReconciler.ReconcileWorkflowOperationAsync(childOperationId, cancellationToken).ConfigureAwait(false);
        var deploy = await _workflowStore.GetAsync(childOperationId, cancellationToken).ConfigureAwait(false);
        if (deploy is null)
        {
            return new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Failed,
                Detail = "Container rollout operation could not be read back."
            };
        }

        return new CoordinatedStepResult
        {
            Outcome = MapOutcome(deploy.Status),
            ChildOperationId = childOperationId,
            Detail = deploy.CurrentPhase
        };
    }

    public async Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        => await deployService.RequestRollbackAsync(
                childOperationId,
                requestedBy: null,
                reason: "Coordinated rollback unwinding the container rollout.",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private static CoordinatedStepOutcome MapOutcome(WorkflowOperationStatus status) => status switch
    {
        WorkflowOperationStatus.Succeeded => CoordinatedStepOutcome.Succeeded,
        WorkflowOperationStatus.Failed or WorkflowOperationStatus.ManualInterventionRequired
            or WorkflowOperationStatus.RolledBack or WorkflowOperationStatus.RollbackRequested => CoordinatedStepOutcome.Failed,
        _ => CoordinatedStepOutcome.Pending
    };
}

/// <summary>
/// Composes the existing additive metadata-release lifecycle (its DB ScriptMigration + metadata-v2
/// activation) as a coordinated-release step. Thin adapter over
/// <see cref="MetadataReleaseControlService"/> and the metadata-release reconciler: it submits the
/// additive metadata-release operation, drives it through its stages, and requests its reversible
/// rollback. It does not reimplement the schema mutation, activation, or inverse.
/// </summary>
internal sealed class CoordinatedMetadataStepExecutor(
    MetadataReleaseControlService metadataControlService,
    IMetadataReleaseOperationReconciler metadataReconciler,
    IEnumerable<IWorkflowOperationStore> workflowStores) : ICoordinatedMetadataStepExecutor
{
    private readonly IWorkflowOperationStore? _workflowStore = workflowStores.FirstOrDefault();

    public async Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        var metadata = await metadataControlService.CreateAsync(
                context.MetadataPlan,
                operation.Audit.RequestedBy,
                $"Coordinated release {operation.OperationId} DB schema + metadata activation.",
                idempotencyKey: $"{operation.OperationId}-metadata",
                correlationId: operation.Audit.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);

        return new CoordinatedStepResult
        {
            Outcome = MapOutcome(metadata.Status),
            ChildOperationId = metadata.OperationId,
            Detail = metadata.CurrentPhase ?? "Metadata/schema lifecycle submitted."
        };
    }

    public async Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null)
        {
            return new CoordinatedStepResult { Outcome = CoordinatedStepOutcome.Pending };
        }

        // Drive the metadata-release lifecycle forward one stage per observe so the coordinated
        // conductor doesn't depend on the metadata background loop timing.
        await metadataReconciler.ReconcileMetadataReleaseAsync(childOperationId, cancellationToken).ConfigureAwait(false);
        var metadata = await _workflowStore.GetAsync(childOperationId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Failed,
                Detail = "Metadata/schema operation could not be read back."
            };
        }

        return new CoordinatedStepResult
        {
            Outcome = MapOutcome(metadata.Status),
            ChildOperationId = childOperationId,
            Detail = metadata.CurrentPhase
        };
    }

    public async Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null)
        {
            return;
        }

        var metadata = await _workflowStore.GetAsync(childOperationId, cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.MetadataRelease is null)
        {
            return;
        }

        // Flip the metadata operation into RollbackRequested and drive its reconciler so it reactivates
        // the prior revision and executes the reversible inverse script. Idempotent.
        if (metadata.Status is not (WorkflowOperationStatus.RolledBack or WorkflowOperationStatus.RollbackRequested))
        {
            var rollbackRequested = metadata with
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Coordinated rollback unwinding the metadata/schema step.",
                MetadataRelease = metadata.MetadataRelease with { CurrentStage = MetadataReleaseStage.RollbackRequested }
            };
            await _workflowStore.SetAsync(rollbackRequested, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await metadataReconciler.ReconcileMetadataReleaseAsync(childOperationId, cancellationToken).ConfigureAwait(false);
    }

    private static CoordinatedStepOutcome MapOutcome(WorkflowOperationStatus status) => status switch
    {
        WorkflowOperationStatus.Succeeded => CoordinatedStepOutcome.Succeeded,
        WorkflowOperationStatus.Failed or WorkflowOperationStatus.ManualInterventionRequired
            or WorkflowOperationStatus.RolledBack or WorkflowOperationStatus.RollbackRequested => CoordinatedStepOutcome.Failed,
        _ => CoordinatedStepOutcome.Pending
    };
}
