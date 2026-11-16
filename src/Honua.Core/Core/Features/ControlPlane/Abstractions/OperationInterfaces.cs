// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IOperationStore
{
    Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowOperationStore : IOperationStore
{
    Task<bool> TryCreateAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowOperationRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetAsync(
        WorkflowOperationRecord operation,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
        WorkflowOperationKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowOperationPage> QueryAsync(
        WorkflowOperationQuery query,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkflowOperationPage
        {
            Items = Array.Empty<WorkflowOperationRecord>(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = 0,
            HasMore = false
        });

    Task<WorkflowOperationRecord?> GetMostRecentSucceededDeployByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<WorkflowOperationRecord?>(null);

    Task<bool?> HasLaterDeployOfTargetAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => Task.FromResult<bool?>(null);
}

public readonly record struct OwnedJobLease(string LeaseOperationId, string OwnerId);

public interface IExecutionJobStore : IOperationStore
{
    Task<bool> TryCreateAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<ExecutionJobRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    // Atomically persists the execution job record only when both its version and the lease owner
    // still match. A store that cannot do both in one atomic operation fails closed.
    Task<bool> TrySetIfLeaseOwnedAsync(
        ExecutionJobRecord job,
        OwnedJobLease lease,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<ExecutionJobPage> QueryAsync(
        ExecutionJobQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        int? limit = null,
        CancellationToken cancellationToken = default);
}

public interface IDeployBackend
{
    string BackendName { get; }

    DeployTargetKind TargetKind { get; }

    Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default);

    Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => ObserveAsync(operation, cancellationToken);

    Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<DeployObservation> CompleteProtectionAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.Succeeded,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = operation.Deploy?.Protection?.CandidateRevision ?? operation.Deploy?.CurrentRevision,
            Message = "Post-activation observation window elapsed."
        });
}

public interface ICheckpointedDeployBackend
{
    public const string PendingPhase = "Deploy preparation pending.";
    public const string PreparingPhase = "Deploy preparation in progress; outcome must be verified if interrupted.";
    public const string PreparedPhase = "Deploy preparation completed and migration outcome recorded.";
    public const string CutoverPhase = "Deploy cutover authorized; submitting traffic shift.";
    public const string RampPhase = "Deploy canary step authorized; submitting traffic shift.";
    public const string ActivatedPhase = "Deploy activation submitted; awaiting convergence.";

    bool RequiresCheckpoint(WorkflowOperationRecord operation);
    Task EnsurePreparationAdmittedAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
        => Task.CompletedTask;
    Task<DeployCheckpoint> PrepareCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken);
    Task<DeployObservation> ActivateCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken);
}

public sealed record DeployCheckpoint
{
    public required string CandidateRevision { get; init; }
    public required string StableRevision { get; init; }
    public required string OriginalServiceRevision { get; init; }
}

public interface IBatchComputeSubmissionPreparer
{
    Task<IReadOnlyDictionary<string, string>?> PrepareSubmissionAsync(ExecutionJobRecord job, CancellationToken cancellationToken);

    Task OnPreparationCommittedAsync(ExecutionJobRecord previous, ExecutionJobRecord prepared, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public interface IBatchComputeBackend
{
    string BackendName { get; }

    BatchComputeTargetKind TargetKind { get; }

    Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);

    Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);

    Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowOperationReconciler
{
    Task ReconcileWorkflowOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}

public interface IMetadataReleaseOperationReconciler
{
    Task ReconcileMetadataReleaseAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}

public interface IExecutionJobReconciler
{
    Task ReconcileExecutionJobAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
