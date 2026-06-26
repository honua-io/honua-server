// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Identifies the family of control-plane operation that a reconcile request targets.
/// The dispatcher uses this to route an <see cref="OperationRef"/> to the matching
/// typed reconciler's idempotent reconcile-once method.
/// </summary>
public enum OperationKind
{
    /// <summary>
    /// Deploy/rollback workflow operations reconciled by
    /// <see cref="IWorkflowOperationReconciler"/>.
    /// </summary>
    DeployWorkflow,

    /// <summary>
    /// Run-to-completion execution jobs (geoprocessing, ETL) reconciled by
    /// <see cref="IExecutionJobReconciler"/>.
    /// </summary>
    ExecutionJob,

    /// <summary>
    /// Additive metadata-release workflow operations reconciled by
    /// <see cref="IMetadataReleaseOperationReconciler"/>.
    /// </summary>
    MetadataRelease,

    /// <summary>
    /// Coordinated platform-upgrade releases reconciled by
    /// <see cref="ICoordinatedReleaseReconciler"/>.
    /// </summary>
    CoordinatedRelease
}

/// <summary>
/// A protocol-neutral reference to a single control-plane operation, sufficient for the
/// dispatcher to route a reconcile request to the correct typed reconciler without the
/// caller knowing which reconciler implements the work.
/// </summary>
/// <param name="Kind">The operation family that owns this operation.</param>
/// <param name="OperationId">The stable durable operation identifier.</param>
public readonly record struct OperationRef(OperationKind Kind, string OperationId);

/// <summary>
/// Single entrypoint that reconciles one control-plane operation exactly once, routing to
/// the typed reconciler that owns the operation's <see cref="OperationKind"/>.
/// <para>
/// This is the seam shared by both trigger styles: the background poll loops and the
/// event handler / backstop sweep all funnel through <see cref="ReconcileOnceAsync"/> so a
/// single idempotent reconcile path serves poll-driven (on-prem) and event-driven (cloud)
/// deployments from one codebase. The per-operation lease + read-then-CAS + terminal guards
/// inside each reconciler make duplicate, concurrent, and out-of-order invocation safe, so
/// the dispatcher itself carries no extra coordination.
/// </para>
/// </summary>
public interface IOperationReconcileDispatcher
{
    /// <summary>
    /// Reconciles the referenced operation once and returns. Routing is by
    /// <see cref="OperationRef.Kind"/>; an empty operation id is a no-op.
    /// </summary>
    /// <param name="op">The operation to reconcile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReconcileOnceAsync(OperationRef op, CancellationToken cancellationToken = default);
}
