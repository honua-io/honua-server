// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Base contract for durable control-plane operation stores backed by shared infrastructure such as Redis.
/// </summary>
public interface IOperationStore
{
    /// <summary>
    /// Attempts to acquire a short-lived lease for reconciling an operation.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="ownerId">Stable reconciler owner identifier.</param>
    /// <param name="leaseDuration">Requested lease duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the lease was acquired; otherwise false.</returns>
    Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an existing reconciliation lease.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="ownerId">Stable reconciler owner identifier.</param>
    /// <param name="leaseDuration">Requested lease duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the lease was renewed; otherwise false.</returns>
    Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an operation lease held by the caller.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="ownerId">Stable reconciler owner identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable store for reconcile-style workflow operations such as deploy and rollback.
/// </summary>
public interface IWorkflowOperationStore : IOperationStore
{
    /// <summary>
    /// Attempts to create a new workflow operation record.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="ttl">Optional retention period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when created; otherwise false if it already exists.</returns>
    Task<bool> TryCreateAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a workflow operation by identifier.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Workflow operation or null when not found.</returns>
    Task<WorkflowOperationRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest workflow operation state.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="ttl">Optional retention period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists active workflow operations, optionally filtered by kind.
    /// </summary>
    /// <param name="kind">Optional workflow kind filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active workflow operations.</returns>
    Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
        WorkflowOperationKind? kind = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable store for run-to-completion execution jobs such as geoprocessing and ETL.
/// </summary>
public interface IExecutionJobStore : IOperationStore
{
    /// <summary>
    /// Attempts to create a new execution job record.
    /// </summary>
    /// <param name="job">Execution job record.</param>
    /// <param name="ttl">Optional retention period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when created; otherwise false if it already exists.</returns>
    Task<bool> TryCreateAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an execution job by identifier.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution job or null when not found.</returns>
    Task<ExecutionJobRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest execution job state.
    /// </summary>
    /// <param name="job">Execution job record.</param>
    /// <param name="ttl">Optional retention period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists active execution jobs, optionally filtered by job kind.
    /// </summary>
    /// <param name="kind">Optional execution job kind filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active execution jobs.</returns>
    Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider adapter for deploy and rollback workflows.
/// </summary>
public interface IDeployBackend
{
    /// <summary>
    /// Provider adapter identifier used in control-plane records.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// Deploy target family implemented by this backend.
    /// </summary>
    DeployTargetKind TargetKind { get; }

    /// <summary>
    /// Returns static and dynamic backend capabilities.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Backend capabilities.</returns>
    Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a deploy before any provider mutation occurs.
    /// </summary>
    /// <param name="spec">Deploy specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Provider-aware deploy plan.</returns>
    Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a workflow operation to the provider backend.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Submission result.</returns>
    Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes the current provider state for a previously submitted workflow operation.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest observation from the provider.</returns>
    Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances a staged rollout after external gates such as telemetry have passed.
    /// Backends without staged traffic shifting can return their normal observation.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest observation from the provider.</returns>
    Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => ObserveAsync(operation, cancellationToken);

    /// <summary>
    /// Requests rollback for a workflow operation when supported by the backend.
    /// </summary>
    /// <param name="operation">Workflow operation record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest observation from the provider.</returns>
    Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider adapter for run-to-completion batch compute workloads.
/// </summary>
public interface IBatchComputeBackend
{
    /// <summary>
    /// Provider adapter identifier used in control-plane records.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// Batch backend family implemented by this adapter.
    /// </summary>
    BatchComputeTargetKind TargetKind { get; }

    /// <summary>
    /// Returns static and dynamic backend capabilities.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Backend capabilities.</returns>
    Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an execution job to the provider backend.
    /// </summary>
    /// <param name="job">Execution job record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Submission result.</returns>
    Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes the current provider state for a submitted execution job.
    /// </summary>
    /// <param name="job">Execution job record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest observation from the provider.</returns>
    Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation for a submitted execution job when supported by the backend.
    /// </summary>
    /// <param name="job">Execution job record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest observation from the provider.</returns>
    Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles workflow operations and execution jobs against external provider state.
/// </summary>
public interface IOperationReconciler
{
    /// <summary>
    /// Reconciles a workflow operation by identifier.
    /// </summary>
    /// <param name="operationId">Workflow operation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReconcileWorkflowOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles an execution job by identifier.
    /// </summary>
    /// <param name="operationId">Execution job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReconcileExecutionJobAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
