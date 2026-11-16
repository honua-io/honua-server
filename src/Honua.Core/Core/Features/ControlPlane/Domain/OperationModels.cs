// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.ControlPlane.Domain;

public enum WorkflowOperationKind
{
    Deploy,

    Rollback,

    Migration,

    MetadataRelease,

    CoordinatedRelease,

    PreMigrationRestore
}

public enum WorkflowOperationStatus
{
    Planned,

    AwaitingApproval,

    Submitted,

    Reconciling,

    Succeeded,

    Failed,

    RollbackRequested,

    RolledBack,

    ManualInterventionRequired
}

public enum MetadataReleaseStage
{
    Preflight,

    Backup,

    ScriptMigration,

    MetadataApply,

    ServicePublication,

    Smoke,

    SloWatch,

    Promotion,

    Complete,

    Failed,

    RollbackRequested
}

public enum MetadataRollbackClass
{
    MetadataOnly,

    AliasRepoint,

    ServiceRevisionRevert,

    ScriptRollback,

    SnapshotRestore,

    ManualRecovery
}

public enum ExecutionJobKind
{
    Geoprocessing,

    ExtractTransformLoad,

    TileCache,

    ShareExport,

    GeocoderBuild,

    RouterBuild,

    TileExport,

    NetworkTopologyRebuild,

    FileImport,

    LayerPublish,

    // Asynchronous admin layer export (CSV, Shapefile, GeoPackage) above the synchronous threshold.
    Export,

    GeoservicesImport,

    GeoServerImport
}

public enum ExecutionJobStatus
{
    Queued,

    Provisioning,

    Running,

    Succeeded,

    Failed,

    Cancelled
}

public enum DeployTargetKind
{
    Kubernetes,

    AwsEcs,

    AzureContainerApps,

    AwsLambda,

    AzureFunctions,

    SelfHostedRolling,

    LocalCompose
}

public enum BatchComputeTargetKind
{
    KubernetesJob,

    AwsBatch,

    AzureBatch,

    LocalProcess
}

public enum OperationPriority
{
    Low,

    Normal,

    High,

    Critical
}

public sealed record OperationAuditInfo
{
    public string? OperationInstanceId { get; init; }

    public string? AuditId { get; init; }

    public string? RequestedBy { get; init; }

    public string? TenantId { get; init; }

    public string? Reason { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    public string? ProposalId { get; init; }

    public string? ApprovedBy { get; init; }

    public string? RequestFingerprint { get; init; }

    public string? ApprovalPolicyRef { get; init; }

    public IReadOnlyList<string> ApprovalReasonCodes { get; init; } = [];

    public CustomCodeOwnerScope? CustomCodeOwnerScope { get; init; }

    public JobSecurityContext? SubmitterSecurityContext { get; init; }
}

public sealed record OperationConcurrencyPolicy
{
    public string? PartitionKey { get; init; }

    public bool RequiresExclusiveLease { get; init; } = true;
}

public sealed record DeployOperationSpec
{
    public required string TargetId { get; init; }

    public required DeployTargetKind TargetKind { get; init; }

    public required string Backend { get; init; }

    public required string Environment { get; init; }

    public required string TargetName { get; init; }

    public string? ArtifactReference { get; init; }

    public string? RuntimeProfile { get; init; }

    public string? CurrentRevision { get; init; }

    public required string DesiredRevision { get; init; }

    public bool RequiresOutOfBandMigrations { get; init; }

    public bool RequiresApproval { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PreMigrationBackupId { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public CanaryRampSpec? CanaryRamp { get; init; }

    public DateTimeOffset? TrafficExposedAt { get; init; }

    public DeployProtectionState? Protection { get; init; }
}

public enum DeployProtectionPhase
{
    Observing,

    Protected,

    Recovering,

    Expired,

    Unavailable
}

public sealed record DeployProtectionState
{
    public required string PreviousRevision { get; init; }

    public required string CandidateRevision { get; init; }

    public required DateTimeOffset FirstExposureAt { get; init; }

    public required DateTimeOffset ObservationDeadline { get; init; }

    public DateTimeOffset? RecoveryDeadline { get; init; }

    public required string PolicyDigest { get; init; }

    public string? ApprovalScope { get; init; }

    public DeployProtectionPhase Phase { get; init; } = DeployProtectionPhase.Observing;

    public string? ReasonCode { get; init; }

    public string? GrantId { get; init; }

    public string? Actor { get; init; }

    public string? TenantId { get; init; }

    public string? PermittedCompensation { get; init; }
}

public static class DeployRecoveryCompensations
{
    public const string RestorePreviousRevision = "restore-previous-revision";

    public static bool IsKnown(string? value)
        => string.Equals(value, RestorePreviousRevision, StringComparison.OrdinalIgnoreCase);
}

public sealed record CanaryRampSpec
{
    public required IReadOnlyList<int> StepWeights { get; init; } = Array.Empty<int>();

    public required TimeSpan StepBakeDuration { get; init; }

    public int CurrentStepIndex { get; init; }

    public DateTimeOffset? CurrentStepStartedAt { get; init; }

    public bool IsValid =>
        StepWeights.Count > 0 &&
        StepBakeDuration > TimeSpan.Zero &&
        StepWeights[^1] == 100 &&
        StepWeights.All(static weight => weight is >= 1 and <= 100) &&
        IsStrictlyIncreasing(StepWeights);

    public int? CurrentStepWeight =>
        CurrentStepIndex >= 0 && CurrentStepIndex < StepWeights.Count
            ? StepWeights[CurrentStepIndex]
            : null;

    public bool IsFinalStep => CurrentStepIndex >= StepWeights.Count - 1;

    private static bool IsStrictlyIncreasing(IReadOnlyList<int> weights)
    {
        for (var index = 1; index < weights.Count; index++)
        {
            if (weights[index] <= weights[index - 1])
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record MetadataReleaseContext
{
    public required string PackageId { get; init; }

    public string? RootProvisioningOperationId { get; init; }

    public string? GitOperationId { get; init; }

    public string? PrUrl { get; init; }

    public string? CommitSha { get; init; }

    public required string DesiredRevision { get; init; }

    public required string TargetEnvironment { get; init; }

    public string? DeployOperationId { get; init; }

    public IReadOnlyList<string> JobIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<MetadataEvidenceRef> EvidenceRefs { get; init; } = Array.Empty<MetadataEvidenceRef>();

    public MetadataReleaseStage CurrentStage { get; init; } = MetadataReleaseStage.Preflight;

    public MetadataRollbackPlan? RollbackPlan { get; init; }

    public MetadataReleaseExecutionPlan? ExecutionPlan { get; init; }

    public long? PriorRevision { get; init; }

    public string? PriorEtag { get; init; }

    public long? CandidateRevision { get; init; }

    public string? CandidateEtag { get; init; }

    public IReadOnlyList<MetadataReleaseScriptOperation> OwnedOperations { get; init; } = Array.Empty<MetadataReleaseScriptOperation>();

    public DateTimeOffset? ActivatedAt { get; init; }

    public int RebaseCount { get; init; }

    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record MetadataRollbackPlan
{
    public required MetadataRollbackClass Class { get; init; }

    public bool IsDataAffecting => Class is not MetadataRollbackClass.MetadataOnly;

    public bool RequiresExplicitApproval { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRequired { get; init; } = Array.Empty<string>();

    public string? ApprovalPolicyRef { get; init; }
}

public sealed record MetadataEvidenceRef
{
    public required string Kind { get; init; }

    public required string RefId { get; init; }

    public string? Uri { get; init; }

    public required DateTimeOffset At { get; init; }
}

public sealed record DeployTargetDefinition
{
    public required string TargetId { get; init; }

    public required DeployTargetKind TargetKind { get; init; }

    public required string Backend { get; init; }

    public required string Environment { get; init; }

    public required string TargetName { get; init; }

    public string? ArtifactReference { get; init; }

    public string? RuntimeProfile { get; init; }

    public bool RequiresApproval { get; init; }

    public bool RequiresOutOfBandMigrations { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed record WorkflowOperationRecord
{
    public required string OperationId { get; init; }

    public required WorkflowOperationKind Kind { get; init; }

    public required WorkflowOperationStatus Status { get; init; }

    public long Version { get; init; }

    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ProviderOperationId { get; init; }

    public string? CurrentPhase { get; init; }

    public string? ObservedState { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    public OperationAuditInfo Audit { get; init; } = new();

    public OperationConcurrencyPolicy Concurrency { get; init; } = new();

    public DeployOperationSpec? Deploy { get; init; }

    public MetadataReleaseContext? MetadataRelease { get; init; }

    public string? RootProvisioningOperationId { get; init; }

    public CoordinatedReleaseContext? CoordinatedRelease { get; init; }

    public PreMigrationRestoreContext? PreMigrationRestore { get; init; }
}

public sealed record PreMigrationRestoreContext
{
    public required string Payload { get; init; }

    public required string Phase { get; init; }

    public required string ApprovedBy { get; init; }

    public string? DeployOperationId { get; init; }

    public int? ExpectedSchema { get; init; }

    public string? RestoredHostHash { get; init; }

    public string? RestoredInstanceId { get; init; }

    public string? RestoredResourceId { get; init; }
}

public sealed record ExecutionJobSpec : Honua.Core.Serialization.ExtensibleJsonRecord
{
    public GPServerJobBinding? GPServerBinding { get; init; }

    public string? WorkloadId { get; init; }

    public required BatchComputeTargetKind TargetKind { get; init; }

    public required string Backend { get; init; }

    public required ExecutionJobKind Kind { get; init; }

    public required string WorkloadName { get; init; }

    public string? Artifact { get; init; }

    public string? RuntimeProfile { get; init; }

    public int ContractVersion { get; init; } = 1;

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed record GPServerJobBinding(string ServiceId, string TaskName, string ProcessId);

public sealed record ExecutionJobDefinition
{
    public required string WorkloadId { get; init; }

    public required BatchComputeTargetKind TargetKind { get; init; }

    public required string Backend { get; init; }

    public required ExecutionJobKind Kind { get; init; }

    public required string WorkloadName { get; init; }

    public string? ArtifactReference { get; init; }

    public string? RuntimeProfile { get; init; }

    public int ContractVersion { get; init; } = 1;

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed record ExecutionJobRecord : Honua.Core.Serialization.ExtensibleJsonRecord
{
    public required string OperationId { get; init; }

    public required ExecutionJobStatus Status { get; init; }

    public long Version { get; init; }

    public long ManualRetryGeneration { get; init; }

    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ProviderOperationId { get; init; }

    public double? PercentComplete { get; init; }

    public string? CurrentPhase { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public OperationAuditInfo Audit { get; init; } = new();

    public OperationConcurrencyPolicy Concurrency { get; init; } = new();

    public required ExecutionJobSpec Spec { get; init; }

    // -----------------------------------------------------------------------
    // Claim and heartbeat fields
    // -----------------------------------------------------------------------

    public string? ClaimedBy { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset? LastHeartbeatAt { get; init; }

    public DateTimeOffset? CancellationRequestedAt { get; init; }

    // -----------------------------------------------------------------------
    // Retry tracking
    // -----------------------------------------------------------------------

    public int AttemptCount { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    // -----------------------------------------------------------------------
    // Policies
    // -----------------------------------------------------------------------

    public JobRetryPolicy? RetryPolicy { get; init; }

    public JobHeartbeatPolicy? HeartbeatPolicy { get; init; }

    public JobTimeoutPolicy? TimeoutPolicy { get; init; }

    // -----------------------------------------------------------------------
    // Artifact references
    // -----------------------------------------------------------------------

    public IReadOnlyList<string> ArtifactReferences { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CleanupArtifactReferences { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CommittedEffectReferences { get; init; } = Array.Empty<string>();

    public double? AdmissionCostWeight { get; init; }

    // The admission rate principal charged for this submission; the per-principal rate window counts
    // the jobs that carry it.
    public string? AdmissionRatePrincipal { get; init; }
}

public sealed record DeployPlan
{
    public bool IsReadyToSubmit { get; init; }

    public bool RequiresApproval { get; init; }

    public bool RequiresOutOfBandMigrations { get; init; }

    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record DeployBackendCapabilities
{
    public bool SupportsRollback { get; init; }

    public bool SupportsCancellation { get; init; }

    public bool SupportsTrafficShifting { get; init; }

    public bool RequiresOutOfBandMigrations { get; init; }

    public bool SupportsProgressPolling { get; init; }

    public bool SupportsRevisionPinning { get; init; }

    public bool StagesCandidateWithoutTraffic { get; init; }
}

public sealed record DeploySubmissionResult
{
    public required WorkflowOperationStatus Status { get; init; }

    public string? ProviderOperationId { get; init; }

    public string? ObservedRevision { get; init; }

    public string? Message { get; init; }
}

public sealed record DeployObservation
{
    public required WorkflowOperationStatus Status { get; init; }

    public string? ProviderOperationId { get; init; }

    public string? ObservedRevision { get; init; }

    public string? Message { get; init; }

    public bool PromotionRecommended { get; init; }

    public bool RollbackRecommended { get; init; }
}

public sealed record BatchComputeBackendCapabilities
{
    public bool SupportsCancellation { get; init; }

    public bool SupportsLogStreaming { get; init; }

    public bool SupportsProgressPolling { get; init; }

    public bool SupportsRetry { get; init; }

    public bool SupportsArtifactStaging { get; init; }

    public int MaxSupportedContractVersion { get; init; } = 1;
}

public sealed record BatchComputeSubmissionResult
{
    public required ExecutionJobStatus Status { get; init; }

    public string? ProviderOperationId { get; init; }

    public string? Message { get; init; }

    public IReadOnlyDictionary<string, string>? ResolvedParameters { get; init; }
}

public sealed record BatchComputeObservation
{
    public int? RecoveredAttemptCount { get; init; }

    public required ExecutionJobStatus Status { get; init; }

    public string? ProviderOperationId { get; init; }

    public double? PercentComplete { get; init; }

    public string? Message { get; init; }
}
