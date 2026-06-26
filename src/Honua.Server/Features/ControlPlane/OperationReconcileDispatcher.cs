// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Routes an <see cref="OperationRef"/> to the typed reconciler that owns its
/// <see cref="OperationKind"/>, calling that reconciler's idempotent reconcile-once method.
/// <para>
/// This is the Phase 0 dispatcher seam: it adds no behavior of its own. The background poll
/// loops and the Phase 1 event handler / backstop sweep both call
/// <see cref="ReconcileOnceAsync"/> so a single reconcile path is exercised regardless of what
/// triggers it. The reconcilers' own lease + CAS + terminal guards keep duplicate and
/// out-of-order invocation safe; the dispatcher deliberately stays a thin switch.
/// </para>
/// </summary>
internal sealed class OperationReconcileDispatcher(
    IWorkflowOperationReconciler deployReconciler,
    IExecutionJobReconciler executionJobReconciler,
    IMetadataReleaseOperationReconciler metadataReleaseReconciler,
    ICoordinatedReleaseReconciler coordinatedReleaseReconciler) : IOperationReconcileDispatcher
{
    public Task ReconcileOnceAsync(OperationRef op, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(op.OperationId))
        {
            return Task.CompletedTask;
        }

        return op.Kind switch
        {
            OperationKind.DeployWorkflow
                => deployReconciler.ReconcileWorkflowOperationAsync(op.OperationId, cancellationToken),
            OperationKind.ExecutionJob
                => executionJobReconciler.ReconcileExecutionJobAsync(op.OperationId, cancellationToken),
            OperationKind.MetadataRelease
                => metadataReleaseReconciler.ReconcileMetadataReleaseAsync(op.OperationId, cancellationToken),
            OperationKind.CoordinatedRelease
                => coordinatedReleaseReconciler.ReconcileCoordinatedReleaseAsync(op.OperationId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(op),
                op.Kind,
                "Unknown control-plane operation kind; no reconciler is registered for this kind.")
        };
    }
}
