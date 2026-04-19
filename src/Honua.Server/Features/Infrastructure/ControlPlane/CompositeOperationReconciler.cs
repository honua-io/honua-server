// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Routes reconciliation to the workflow or execution-job reconciler based on operation kind.
/// </summary>
internal sealed class CompositeOperationReconciler(
    DeployWorkflowReconciler workflowReconciler,
    ExecutionJobReconciler executionJobReconciler) : IOperationReconciler
{
    public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
        => workflowReconciler.ReconcileWorkflowOperationAsync(operationId, cancellationToken);

    public Task ReconcileExecutionJobAsync(string operationId, CancellationToken cancellationToken = default)
        => executionJobReconciler.ReconcileAsync(operationId, cancellationToken);
}
