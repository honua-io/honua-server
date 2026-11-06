// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Admin.Models;

public sealed class DeployPreflightResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("readyForCoordinatedDeploy")]
    public bool ReadyForCoordinatedDeploy { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string? ServerVersion { get; init; }

    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    [JsonPropertyName("deploymentMode")]
    public string? DeploymentMode { get; init; }

    [JsonPropertyName("instanceName")]
    public string? InstanceName { get; init; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("readiness")]
    public DeployPreflightReadiness? Readiness { get; init; }

    [JsonPropertyName("migration")]
    public DeployPreflightMigration? Migration { get; init; }

    [JsonPropertyName("databaseCompatibility")]
    public DeployPreflightDatabaseCompatibility? DatabaseCompatibility { get; init; }

    [JsonPropertyName("databaseSessionSettings")]
    public DeployPreflightDatabaseSessionSettings? DatabaseSessionSettings { get; init; }

    [JsonPropertyName("platformRelease")]
    public DeployPreflightPlatformRelease? PlatformRelease { get; init; }
}

public sealed class DeployPreflightPlatformRelease
{
    [JsonPropertyName("evidencePosture")]
    public EvidencePosture? EvidencePosture { get; init; }

    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("releaseDeclared")]
    public bool ReleaseDeclared { get; init; }

    [JsonPropertyName("isCoVersioned")]
    public bool IsCoVersioned { get; init; }

    [JsonPropertyName("serving")]
    public IReadOnlyList<DeployPreflightPlaneProjection> Serving { get; init; } = Array.Empty<DeployPreflightPlaneProjection>();

    [JsonPropertyName("execution")]
    public IReadOnlyList<DeployPreflightPlaneProjection> Execution { get; init; } = Array.Empty<DeployPreflightPlaneProjection>();

    [JsonPropertyName("skewedIds")]
    public IReadOnlyList<string> SkewedIds { get; init; } = Array.Empty<string>();
}

public sealed class DeployPreflightPlaneProjection
{
    [JsonPropertyName("observedArtifactReference")]
    public string? ObservedArtifactReference { get; init; }

    [JsonPropertyName("observedImageId")]
    public string? ObservedImageId { get; init; }

    [JsonPropertyName("serverVersion")]
    public string? ServerVersion { get; init; }

    // Configuration is never reported as a live observation.
    [JsonPropertyName("observationStatus")]
    public string? ObservationStatus { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("runtimeProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeProfile { get; init; }

    [JsonPropertyName("effectiveArtifactReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EffectiveArtifactReference { get; init; }

    [JsonPropertyName("projectedFromRelease")]
    public bool ProjectedFromRelease { get; init; }

    [JsonPropertyName("skewed")]
    public bool Skewed { get; init; }
}

public sealed class DeployPreflightReadiness
{
    [JsonPropertyName("isReady")]
    public bool IsReady { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

public sealed class DeployPreflightMigration
{
    [JsonPropertyName("lifecycleStatus")]
    public string LifecycleStatus { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("planAvailable")]
    public bool PlanAvailable { get; init; }

    [JsonPropertyName("upgradeRequired")]
    public bool UpgradeRequired { get; init; }

    [JsonPropertyName("pendingScripts")]
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    [JsonPropertyName("pendingScriptClassifications")]
    public IReadOnlyList<DeployPreflightMigrationClassification> PendingScriptClassifications { get; init; }
        = Array.Empty<DeployPreflightMigrationClassification>();

    [JsonPropertyName("scriptBindings")]
    public IReadOnlyList<DatabaseMigrationScriptBinding> ScriptBindings { get; init; }
        = Array.Empty<DatabaseMigrationScriptBinding>();

    [JsonPropertyName("contractApprovalNonce")]
    public string? ContractApprovalNonce { get; init; }

    [JsonPropertyName("executedButNotDiscoveredScripts")]
    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    [JsonPropertyName("planError")]
    public string? PlanError { get; init; }

    [JsonPropertyName("backupHook")]
    public MigrationBackupHookStatus? BackupHook { get; init; }
}

public sealed class DeployPreflightMigrationClassification
{
    [JsonPropertyName("scriptName")]
    public string ScriptName { get; init; } = string.Empty;

    [JsonPropertyName("classification")]
    public string Classification { get; init; } = string.Empty;

    [JsonPropertyName("breakingRules")]
    public IReadOnlyList<string> BreakingRules { get; init; } = Array.Empty<string>();
}

public sealed class DeployPreflightDatabaseCompatibility
{
    [JsonPropertyName("isCompatible")]
    public bool IsCompatible { get; init; }

    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; init; } = string.Empty;

    [JsonPropertyName("postGisVersion")]
    public string? PostGisVersion { get; init; }

    [JsonPropertyName("postGisRasterVersion")]
    public string? PostGisRasterVersion { get; init; }

    [JsonPropertyName("installedExtensions")]
    public IReadOnlyList<string> InstalledExtensions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

public sealed class DeployPreflightDatabaseSessionSettings
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("verification")]
    public string Verification { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("remediation")]
    public IReadOnlyList<string> Remediation { get; init; } = Array.Empty<string>();

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class DeployPlanRequest
{
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("desiredRevision")]
    public string DesiredRevision { get; init; } = string.Empty;

    [JsonPropertyName("currentRevision")]
    public string? CurrentRevision { get; init; }

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public sealed class CreateDeployOperationRequest
{
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("desiredRevision")]
    public string DesiredRevision { get; init; } = string.Empty;

    [JsonPropertyName("currentRevision")]
    public string? CurrentRevision { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("submitImmediately")]
    public bool? SubmitImmediately { get; init; }

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

// A rollback body that supplies any fence term is asserting a grant, and a term that does not match the recorded activation refuses the rollback rather than being ignored.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RollbackDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }

    [JsonPropertyName("expectedCandidateRevision")]
    public string? ExpectedCandidateRevision { get; init; }

    [JsonPropertyName("expectedPreviousRevision")]
    public string? ExpectedPreviousRevision { get; init; }

    [JsonPropertyName("expectedProtectionPhase")]
    public string? ExpectedProtectionPhase { get; init; }

    [JsonPropertyName("grantId")]
    public string? GrantId { get; init; }

    [JsonPropertyName("policyDigest")]
    public string? PolicyDigest { get; init; }

    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("notAfter")]
    public DateTimeOffset? NotAfter { get; init; }

    [JsonPropertyName("compensation")]
    public string? Compensation { get; init; }

    [JsonIgnore]
    public bool HasFence =>
        !string.IsNullOrWhiteSpace(TargetId) ||
        !string.IsNullOrWhiteSpace(ExpectedCandidateRevision) ||
        !string.IsNullOrWhiteSpace(ExpectedPreviousRevision) ||
        !string.IsNullOrWhiteSpace(ExpectedProtectionPhase) ||
        !string.IsNullOrWhiteSpace(GrantId) ||
        !string.IsNullOrWhiteSpace(PolicyDigest) ||
        !string.IsNullOrWhiteSpace(Actor) ||
        !string.IsNullOrWhiteSpace(TenantId) ||
        !string.IsNullOrWhiteSpace(Compensation) ||
        NotAfter.HasValue;

    [JsonIgnore]
    public bool HasProtectionBoundFence =>
        !string.IsNullOrWhiteSpace(ExpectedCandidateRevision) ||
        !string.IsNullOrWhiteSpace(ExpectedPreviousRevision) ||
        !string.IsNullOrWhiteSpace(ExpectedProtectionPhase) ||
        !string.IsNullOrWhiteSpace(GrantId) ||
        !string.IsNullOrWhiteSpace(PolicyDigest) ||
        !string.IsNullOrWhiteSpace(Compensation);
}

// The endpoint converges only to the release declared in configuration and never resolves a version to an artifact; a mismatched expected release is a 409.
public sealed class PlatformReleaseConvergeRequest
{
    [JsonPropertyName("targetVersion")]
    public string? TargetVersion { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}

public sealed class PlatformReleaseConvergeResponse
{
    [JsonPropertyName("releaseVersion")]
    public string ReleaseVersion { get; init; } = string.Empty;

    [JsonPropertyName("servingArtifactReference")]
    public string? ServingArtifactReference { get; init; }

    [JsonPropertyName("converged")]
    public bool Converged { get; init; }

    // This API does not deploy geoprocessing worker images; workers converge at the next dispatch.
    [JsonPropertyName("workersDeferred")]
    public bool WorkersDeferred { get; init; } = true;

    [JsonPropertyName("workerConvergenceNote")]
    public string WorkerConvergenceNote { get; init; } = string.Empty;

    [JsonPropertyName("targets")]
    public IReadOnlyList<PlatformReleaseConvergeTargetOutcome> Targets { get; init; } = Array.Empty<PlatformReleaseConvergeTargetOutcome>();

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class PlatformReleaseConvergeTargetOutcome
{
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }

    [JsonPropertyName("lastAppliedRevision")]
    public string? LastAppliedRevision { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class SubmitDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class PromoteDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class DeployPlanResponse
{
    [JsonPropertyName("target")]
    public required DeployPlanTargetResponse Target { get; init; }

    [JsonPropertyName("readyToSubmit")]
    public bool ReadyToSubmit { get; init; }

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; init; }

    [JsonPropertyName("requiresOutOfBandMigrations")]
    public bool RequiresOutOfBandMigrations { get; init; }

    [JsonPropertyName("backendRegistered")]
    public bool BackendRegistered { get; init; }

    [JsonPropertyName("capabilities")]
    public DeployBackendCapabilitiesResponse? Capabilities { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonPropertyName("blockingReasons")]
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class DeployPlanTargetResponse
{
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; init; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    [JsonPropertyName("targetName")]
    public string TargetName { get; init; } = string.Empty;

    [JsonPropertyName("artifactReference")]
    public string? ArtifactReference { get; init; }

    [JsonPropertyName("runtimeProfile")]
    public string? RuntimeProfile { get; init; }

    [JsonPropertyName("currentRevision")]
    public string? CurrentRevision { get; init; }

    [JsonPropertyName("desiredRevision")]
    public string DesiredRevision { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed class DeployBackendCapabilitiesResponse
{
    [JsonPropertyName("supportsRollback")]
    public bool SupportsRollback { get; init; }

    [JsonPropertyName("supportsCancellation")]
    public bool SupportsCancellation { get; init; }

    [JsonPropertyName("supportsTrafficShifting")]
    public bool SupportsTrafficShifting { get; init; }

    [JsonPropertyName("requiresOutOfBandMigrations")]
    public bool RequiresOutOfBandMigrations { get; init; }

    [JsonPropertyName("supportsProgressPolling")]
    public bool SupportsProgressPolling { get; init; }

    [JsonPropertyName("supportsRevisionPinning")]
    public bool SupportsRevisionPinning { get; init; }
}

public sealed class DeployOperationResponse
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public DeployPlanTargetResponse? Target { get; init; }

    [JsonPropertyName("metadataRelease")]
    public MetadataReleaseContextResponse? MetadataRelease { get; init; }

    [JsonPropertyName("providerOperationId")]
    public string? ProviderOperationId { get; init; }

    [JsonPropertyName("currentPhase")]
    public string? CurrentPhase { get; init; }

    [JsonPropertyName("observedState")]
    public string? ObservedState { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonPropertyName("blockingReasons")]
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("rootProvisioningOperationId")]
    public string? RootProvisioningOperationId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("protection")]
    public DeployProtectionResponse? Protection { get; init; }
}

public sealed class DeployProtectionResponse
{
    [JsonPropertyName("previousRevision")]
    public string PreviousRevision { get; init; } = string.Empty;

    [JsonPropertyName("candidateRevision")]
    public string CandidateRevision { get; init; } = string.Empty;

    [JsonPropertyName("firstExposureAt")]
    public DateTimeOffset FirstExposureAt { get; init; }

    [JsonPropertyName("observationDeadline")]
    public DateTimeOffset ObservationDeadline { get; init; }

    [JsonPropertyName("recoveryDeadline")]
    public DateTimeOffset? RecoveryDeadline { get; init; }

    [JsonPropertyName("policyDigest")]
    public string PolicyDigest { get; init; } = string.Empty;

    [JsonPropertyName("approvalScope")]
    public string? ApprovalScope { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = string.Empty;

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; init; }

    [JsonPropertyName("grantId")]
    public string? GrantId { get; init; }

    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("permittedCompensation")]
    public string? PermittedCompensation { get; init; }
}

public sealed class DeployOperationListResponse
{
    [JsonPropertyName("evidencePosture")]
    public EvidencePosture? EvidencePosture { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<DeployOperationResponse> Items { get; init; } = Array.Empty<DeployOperationResponse>();

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

public sealed class MetadataReleaseContextResponse
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; init; } = string.Empty;

    [JsonPropertyName("rootProvisioningOperationId")]
    public string? RootProvisioningOperationId { get; init; }

    [JsonPropertyName("gitOperationId")]
    public string? GitOperationId { get; init; }

    [JsonPropertyName("prUrl")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("desiredRevision")]
    public string DesiredRevision { get; init; } = string.Empty;

    [JsonPropertyName("targetEnvironment")]
    public string TargetEnvironment { get; init; } = string.Empty;

    [JsonPropertyName("deployOperationId")]
    public string? DeployOperationId { get; init; }

    [JsonPropertyName("jobIds")]
    public IReadOnlyList<string> JobIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("evidenceRefs")]
    public IReadOnlyList<MetadataEvidenceRefResponse> EvidenceRefs { get; init; } = Array.Empty<MetadataEvidenceRefResponse>();

    [JsonPropertyName("currentStage")]
    public string CurrentStage { get; init; } = string.Empty;

    [JsonPropertyName("rollbackPlan")]
    public MetadataRollbackPlanResponse? RollbackPlan { get; init; }

    [JsonPropertyName("blockers")]
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class MetadataRollbackPlanResponse
{
    [JsonPropertyName("class")]
    public string Class { get; init; } = string.Empty;

    [JsonPropertyName("isDataAffecting")]
    public bool IsDataAffecting { get; init; }

    [JsonPropertyName("requiresExplicitApproval")]
    public bool RequiresExplicitApproval { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    [JsonPropertyName("evidenceRequired")]
    public IReadOnlyList<string> EvidenceRequired { get; init; } = Array.Empty<string>();

    [JsonPropertyName("approvalPolicyRef")]
    public string? ApprovalPolicyRef { get; init; }
}

public sealed class MetadataEvidenceRefResponse
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("refId")]
    public string RefId { get; init; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }
}
