// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

internal sealed class OperationReconcileDispatcher(
    IWorkflowOperationReconciler deployReconciler,
    IExecutionJobReconciler executionJobReconciler,
    IMetadataReleaseOperationReconciler metadataReleaseReconciler,
    ICoordinatedReleaseReconciler coordinatedReleaseReconciler,
    IPreMigrationRestoreReconciler? restoreReconciler = null) : IOperationReconcileDispatcher
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
            OperationKind.PreMigrationRestore when restoreReconciler is not null
                => restoreReconciler.ReconcileAsync(op.OperationId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(op),
                op.Kind,
                "Unknown control-plane operation kind; no reconciler is registered for this kind.")
        };
    }
}
