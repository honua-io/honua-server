// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Workflow-style control-plane operations that converge toward a desired state.
/// </summary>
public enum WorkflowOperationKind
{
    /// <summary>
    /// Deploy a target revision or version.
    /// </summary>
    Deploy,

    /// <summary>
    /// Roll back to a known-good revision or version.
    /// </summary>
    Rollback,

    /// <summary>
    /// Coordinate a migration or schema gate outside the request path.
    /// </summary>
    Migration
}

/// <summary>
/// Status values for reconciled workflow operations.
/// </summary>
public enum WorkflowOperationStatus
{
    /// <summary>
    /// Operation has been planned but not yet submitted.
    /// </summary>
    Planned,

    /// <summary>
    /// Operation is blocked pending an explicit approval step.
    /// </summary>
    AwaitingApproval,

    /// <summary>
    /// Operation was submitted to the provider backend.
    /// </summary>
    Submitted,

    /// <summary>
    /// Operation is actively reconciling desired and observed state.
    /// </summary>
    Reconciling,

    /// <summary>
    /// Operation reached the desired outcome successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Operation failed and requires investigation.
    /// </summary>
    Failed,

    /// <summary>
    /// Rollback was requested and is waiting to be observed.
    /// </summary>
    RollbackRequested,

    /// <summary>
    /// Operation was rolled back successfully.
    /// </summary>
    RolledBack,

    /// <summary>
    /// Operation needs explicit operator action beyond automatic reconciliation.
    /// </summary>
    ManualInterventionRequired
}

/// <summary>
/// Batch-style execution jobs that run to completion.
/// </summary>
public enum ExecutionJobKind
{
    /// <summary>
    /// Geoprocessing or analytical compute workload.
    /// </summary>
    Geoprocessing,

    /// <summary>
    /// Extract-transform-load or data movement workload.
    /// </summary>
    ExtractTransformLoad,

    /// <summary>
    /// Tile cache generation or refresh workload.
    /// </summary>
    TileCache
}

/// <summary>
/// Status values for provider-backed execution jobs.
/// </summary>
public enum ExecutionJobStatus
{
    /// <summary>
    /// Job is queued and not yet running.
    /// </summary>
    Queued,

    /// <summary>
    /// Provider resources are being provisioned.
    /// </summary>
    Provisioning,

    /// <summary>
    /// Job is actively running.
    /// </summary>
    Running,

    /// <summary>
    /// Job completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Job failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Job was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Supported deploy target families.
/// </summary>
public enum DeployTargetKind
{
    /// <summary>
    /// Kubernetes-based rollout target.
    /// </summary>
    Kubernetes,

    /// <summary>
    /// AWS ECS target using ALB traffic shifting and service-level rollout semantics.
    /// </summary>
    AwsEcs,

    /// <summary>
    /// Azure Container Apps target using revision-based rollout semantics.
    /// </summary>
    AzureContainerApps,

    /// <summary>
    /// AWS Lambda target using revisions, aliases, or traffic shifting.
    /// </summary>
    AwsLambda,

    /// <summary>
    /// Azure Functions target using deployment slots or revision swaps.
    /// </summary>
    AzureFunctions
}

/// <summary>
/// Supported batch compute backend families.
/// </summary>
public enum BatchComputeTargetKind
{
    /// <summary>
    /// Kubernetes Job backend.
    /// </summary>
    KubernetesJob,

    /// <summary>
    /// AWS Batch backend.
    /// </summary>
    AwsBatch
}

/// <summary>
/// Relative operator priority for workflow or execution requests.
/// </summary>
public enum OperationPriority
{
    /// <summary>
    /// Low-priority background work.
    /// </summary>
    Low,

    /// <summary>
    /// Default operational priority.
    /// </summary>
    Normal,

    /// <summary>
    /// Elevated operational priority.
    /// </summary>
    High,

    /// <summary>
    /// Highest operational priority.
    /// </summary>
    Critical
}

/// <summary>
/// Operator identity and request metadata captured when an operation is created.
/// </summary>
public sealed record OperationAuditInfo
{
    /// <summary>
    /// Operator or service principal that requested the operation.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Free-form operator reason or change note.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Stable idempotency key used to deduplicate submissions.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Correlation ID propagated from upstream systems.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Stable fingerprint of the deploy request payload used to validate idempotent replays.
    /// </summary>
    public string? RequestFingerprint { get; init; }

    /// <summary>
    /// Reference to the approval policy that was evaluated, if any.
    /// </summary>
    public string? ApprovalPolicyRef { get; init; }

    /// <summary>
    /// Machine-readable reason codes from the approval evaluation.
    /// </summary>
    public IReadOnlyList<string> ApprovalReasonCodes { get; init; } = [];
}

/// <summary>
/// Coordination hints for operations that should serialize by environment or target.
/// </summary>
public sealed record OperationConcurrencyPolicy
{
    /// <summary>
    /// Partition key used for distributed locking or serialization.
    /// </summary>
    public string? PartitionKey { get; init; }

    /// <summary>
    /// Whether the operation requires exclusive access to its partition.
    /// </summary>
    public bool RequiresExclusiveLease { get; init; } = true;
}

/// <summary>
/// Deploy-specific desired state for a workflow operation.
/// </summary>
public sealed record DeployOperationSpec
{
    /// <summary>
    /// Stable target identifier resolved through the deploy target registry.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Target deployment backend family.
    /// </summary>
    public required DeployTargetKind TargetKind { get; init; }

    /// <summary>
    /// Backend name or provider adapter identifier.
    /// </summary>
    public required string Backend { get; init; }

    /// <summary>
    /// Environment or stage being changed.
    /// </summary>
    public required string Environment { get; init; }

    /// <summary>
    /// Logical target name such as cluster/service/function/app.
    /// </summary>
    public required string TargetName { get; init; }

    /// <summary>
    /// Resolved image, artifact, or package base reference for this operation.
    /// </summary>
    public string? ArtifactReference { get; init; }

    /// <summary>
    /// Optional specialized worker or runtime profile resolved for this operation.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Currently observed revision, alias, or image before the change.
    /// </summary>
    public string? CurrentRevision { get; init; }

    /// <summary>
    /// Desired revision, version, image, or artifact after the change.
    /// </summary>
    public required string DesiredRevision { get; init; }

    /// <summary>
    /// Whether migrations must be managed outside the deploy workflow.
    /// </summary>
    public bool RequiresOutOfBandMigrations { get; init; }

    /// <summary>
    /// Whether the operation should stop for explicit approval before submission.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Opaque backend-specific parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Stable deploy target definition used to resolve provider-specific metadata for workflow operations.
/// </summary>
public sealed record DeployTargetDefinition
{
    /// <summary>
    /// Stable identifier used by the control-plane API and workers.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Deploy backend family used by this target.
    /// </summary>
    public required DeployTargetKind TargetKind { get; init; }

    /// <summary>
    /// Backend adapter identifier that should execute operations for this target.
    /// </summary>
    public required string Backend { get; init; }

    /// <summary>
    /// Environment or stage represented by this target.
    /// </summary>
    public required string Environment { get; init; }

    /// <summary>
    /// Human-readable target name such as cluster/service/function/app.
    /// </summary>
    public required string TargetName { get; init; }

    /// <summary>
    /// Stable artifact or image base reference for this target.
    /// </summary>
    public string? ArtifactReference { get; init; }

    /// <summary>
    /// Optional specialized worker or runtime profile for this target.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Whether deploys for this target require explicit approval before submission.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether schema migrations must be handled outside the deploy workflow for this target.
    /// </summary>
    public bool RequiresOutOfBandMigrations { get; init; }

    /// <summary>
    /// Provider-specific metadata such as namespace, release, alias, or function name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Durable workflow operation record used by the control plane.
/// </summary>
public sealed record WorkflowOperationRecord
{
    /// <summary>
    /// Stable workflow operation identifier.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Workflow operation kind.
    /// </summary>
    public required WorkflowOperationKind Kind { get; init; }

    /// <summary>
    /// Current workflow status.
    /// </summary>
    public required WorkflowOperationStatus Status { get; init; }

    /// <summary>
    /// Relative operator priority.
    /// </summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    /// <summary>
    /// Time when the workflow operation record was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time when the workflow record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Time when the workflow reached a terminal state.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Provider operation ID, deployment ID, or rollout ID when available.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Current operator-facing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Current observed state from the provider.
    /// </summary>
    public string? ObservedState { get; init; }

    /// <summary>
    /// Error message for failed workflow operations.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warning messages collected during reconciliation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Blocking reasons that currently prevent the workflow from being submitted or reconciled.
    /// </summary>
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Request metadata captured when the workflow was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// Coordination and lease policy for this operation.
    /// </summary>
    public OperationConcurrencyPolicy Concurrency { get; init; } = new();

    /// <summary>
    /// Deploy-specific desired state for deploy or rollback workflows.
    /// </summary>
    public DeployOperationSpec? Deploy { get; init; }
}

/// <summary>
/// Batch-compute execution specification.
/// </summary>
public sealed record ExecutionJobSpec
{
    /// <summary>
    /// Stable workload definition identifier resolved through the job-definition registry.
    /// </summary>
    public string? WorkloadId { get; init; }

    /// <summary>
    /// Target batch backend family.
    /// </summary>
    public required BatchComputeTargetKind TargetKind { get; init; }

    /// <summary>
    /// Backend name or provider adapter identifier.
    /// </summary>
    public required string Backend { get; init; }

    /// <summary>
    /// Workload category executed by the backend.
    /// </summary>
    public required ExecutionJobKind Kind { get; init; }

    /// <summary>
    /// Human-readable workload name.
    /// </summary>
    public required string WorkloadName { get; init; }

    /// <summary>
    /// Container image or backend-specific artifact reference.
    /// </summary>
    public string? Artifact { get; init; }

    /// <summary>
    /// Optional specialized worker or runtime profile resolved for this job.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Opaque backend-specific parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Stable workload definition used to resolve specialized compute images and runtime settings.
/// </summary>
public sealed record ExecutionJobDefinition
{
    /// <summary>
    /// Stable workload identifier used by the control plane and job workers.
    /// </summary>
    public required string WorkloadId { get; init; }

    /// <summary>
    /// Batch backend family used by this workload.
    /// </summary>
    public required BatchComputeTargetKind TargetKind { get; init; }

    /// <summary>
    /// Backend adapter identifier that should execute this workload.
    /// </summary>
    public required string Backend { get; init; }

    /// <summary>
    /// Workload category executed by the backend.
    /// </summary>
    public required ExecutionJobKind Kind { get; init; }

    /// <summary>
    /// Human-readable workload name.
    /// </summary>
    public required string WorkloadName { get; init; }

    /// <summary>
    /// Stable image, package, or artifact reference for this workload.
    /// </summary>
    public string? ArtifactReference { get; init; }

    /// <summary>
    /// Optional specialized worker or runtime profile such as a Python/GDAL image family.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Provider-specific workload metadata such as queue, CPU, memory, or timeout.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Durable execution-job record for run-to-completion workloads.
/// </summary>
public sealed record ExecutionJobRecord
{
    /// <summary>
    /// Stable execution job identifier.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Current execution job status.
    /// </summary>
    public required ExecutionJobStatus Status { get; init; }

    /// <summary>
    /// Relative operator priority.
    /// </summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    /// <summary>
    /// Time when the job was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time when the job record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Time when the job reached a terminal state.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Provider job ID when available.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Reported completion percentage when the backend can provide it.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// Current operator-facing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Error message for failed jobs.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warning messages collected during execution.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Request metadata captured when the job was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// Coordination and lease policy for this job.
    /// </summary>
    public OperationConcurrencyPolicy Concurrency { get; init; } = new();

    /// <summary>
    /// Backend execution specification.
    /// </summary>
    public required ExecutionJobSpec Spec { get; init; }

    // -----------------------------------------------------------------------
    // Claim and heartbeat fields (ticket #681)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stable identifier of the worker that claimed this job for execution.
    /// Null when the job has not been claimed.
    /// </summary>
    public string? ClaimedBy { get; init; }

    /// <summary>
    /// Time when the job was claimed by a worker.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>
    /// Time of the most recent heartbeat signal from the executing worker.
    /// </summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>
    /// Time when cancellation was durably requested via the API. Workers observe
    /// this signal during heartbeat and cancel locally; the reconciler honours it
    /// when the worker's heartbeat expires before the signal is processed.
    /// </summary>
    public DateTimeOffset? CancellationRequestedAt { get; init; }

    // -----------------------------------------------------------------------
    // Retry tracking (ticket #681)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Number of execution attempts completed so far (including the current attempt).
    /// </summary>
    public int AttemptCount { get; init; }

    /// <summary>
    /// Earliest time the next retry attempt may begin.
    /// Null when no retry is pending.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; init; }

    // -----------------------------------------------------------------------
    // Policies (ticket #681)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Retry policy governing failure retry behavior. Null uses the system default.
    /// </summary>
    public JobRetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Heartbeat policy governing worker liveness detection. Null uses the system default.
    /// </summary>
    public JobHeartbeatPolicy? HeartbeatPolicy { get; init; }

    /// <summary>
    /// Timeout policy governing maximum execution duration. Null uses the system default.
    /// </summary>
    public JobTimeoutPolicy? TimeoutPolicy { get; init; }

    // -----------------------------------------------------------------------
    // Artifact references (ticket #681)
    // -----------------------------------------------------------------------

    /// <summary>
    /// References to artifacts produced during execution, appended by the worker
    /// through the execution context.
    /// </summary>
    public IReadOnlyList<string> ArtifactReferences { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Provider-neutral deploy plan returned before a workflow is submitted.
/// </summary>
public sealed record DeployPlan
{
    /// <summary>
    /// Whether the deploy can be submitted immediately.
    /// </summary>
    public bool IsReadyToSubmit { get; init; }

    /// <summary>
    /// Whether explicit approval is required before submit.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether the target requires migrations to run out-of-band.
    /// </summary>
    public bool RequiresOutOfBandMigrations { get; init; }

    /// <summary>
    /// Blocking reasons that prevent submission.
    /// </summary>
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Non-blocking warnings for operators.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Capabilities for deploy backends.
/// </summary>
public sealed record DeployBackendCapabilities
{
    /// <summary>
    /// Whether the backend can roll back to a previous revision.
    /// </summary>
    public bool SupportsRollback { get; init; }

    /// <summary>
    /// Whether the backend can cancel an in-flight deploy.
    /// </summary>
    public bool SupportsCancellation { get; init; }

    /// <summary>
    /// Whether the backend supports partial traffic shifting.
    /// </summary>
    public bool SupportsTrafficShifting { get; init; }

    /// <summary>
    /// Whether migrations must run outside the deploy workflow.
    /// </summary>
    public bool RequiresOutOfBandMigrations { get; init; }

    /// <summary>
    /// Whether the backend exposes pollable progress or rollout state.
    /// </summary>
    public bool SupportsProgressPolling { get; init; }

    /// <summary>
    /// Whether the backend can pin or restore a specific revision.
    /// </summary>
    public bool SupportsRevisionPinning { get; init; }
}

/// <summary>
/// Result of submitting a deploy workflow to a provider backend.
/// </summary>
public sealed record DeploySubmissionResult
{
    /// <summary>
    /// Initial workflow status after submission.
    /// </summary>
    public required WorkflowOperationStatus Status { get; init; }

    /// <summary>
    /// Provider-specific deployment or rollout identifier.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Previously observed provider revision before submission when the backend can determine it.
    /// </summary>
    public string? ObservedRevision { get; init; }

    /// <summary>
    /// Operator-facing message from submission.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Observation returned by a deploy backend during reconciliation.
/// </summary>
public sealed record DeployObservation
{
    /// <summary>
    /// Observed workflow status.
    /// </summary>
    public required WorkflowOperationStatus Status { get; init; }

    /// <summary>
    /// Provider-specific deployment or rollout identifier.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Currently observed provider revision or alias.
    /// </summary>
    public string? ObservedRevision { get; init; }

    /// <summary>
    /// Operator-facing message from observation.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether the backend has reached a staged state that is ready for final promotion.
    /// </summary>
    public bool PromotionRecommended { get; init; }

    /// <summary>
    /// Whether the backend recommends rollback based on observed state.
    /// </summary>
    public bool RollbackRecommended { get; init; }
}

/// <summary>
/// Capabilities for batch-compute backends.
/// </summary>
public sealed record BatchComputeBackendCapabilities
{
    /// <summary>
    /// Whether the backend can cancel submitted jobs.
    /// </summary>
    public bool SupportsCancellation { get; init; }

    /// <summary>
    /// Whether the backend can stream live logs.
    /// </summary>
    public bool SupportsLogStreaming { get; init; }

    /// <summary>
    /// Whether the backend exposes pollable job progress.
    /// </summary>
    public bool SupportsProgressPolling { get; init; }

    /// <summary>
    /// Whether failed jobs can be retried through the backend.
    /// </summary>
    public bool SupportsRetry { get; init; }

    /// <summary>
    /// Whether the backend can stage or materialize workload artifacts.
    /// </summary>
    public bool SupportsArtifactStaging { get; init; }
}

/// <summary>
/// Result of submitting an execution job to a provider backend.
/// </summary>
public sealed record BatchComputeSubmissionResult
{
    /// <summary>
    /// Initial execution job status after submission.
    /// </summary>
    public required ExecutionJobStatus Status { get; init; }

    /// <summary>
    /// Provider-specific job identifier.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Operator-facing message from submission.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Observation returned by a batch-compute backend during reconciliation.
/// </summary>
public sealed record BatchComputeObservation
{
    /// <summary>
    /// Observed execution status.
    /// </summary>
    public required ExecutionJobStatus Status { get; init; }

    /// <summary>
    /// Provider-specific job identifier.
    /// </summary>
    public string? ProviderOperationId { get; init; }

    /// <summary>
    /// Observed completion percentage when available.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// Operator-facing observation message.
    /// </summary>
    public string? Message { get; init; }
}
