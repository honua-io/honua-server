// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Instance-scoped deploy preflight response for coordinated Honua rollouts.
/// </summary>
public sealed class DeployPreflightResponse
{
    /// <summary>
    /// Current status for coordinated deployment eligibility.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Whether this instance is ready to participate in a coordinated deployment.
    /// </summary>
    [JsonPropertyName("readyForCoordinatedDeploy")]
    public bool ReadyForCoordinatedDeploy { get; init; }

    /// <summary>
    /// Operator-facing summary for the current preflight result.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Honua server version currently running on this instance.
    /// </summary>
    [JsonPropertyName("serverVersion")]
    public string? ServerVersion { get; init; }

    /// <summary>
    /// ASP.NET host environment for this instance.
    /// </summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>
    /// Deployment mode configured for this instance.
    /// </summary>
    [JsonPropertyName("deploymentMode")]
    public string? DeploymentMode { get; init; }

    /// <summary>
    /// Machine or instance name serving the request.
    /// </summary>
    [JsonPropertyName("instanceName")]
    public string? InstanceName { get; init; }

    /// <summary>
    /// Timestamp when the preflight payload was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Current readiness state for this instance.
    /// </summary>
    [JsonPropertyName("readiness")]
    public DeployPreflightReadiness? Readiness { get; init; }

    /// <summary>
    /// Current migration and schema alignment state for this instance.
    /// </summary>
    [JsonPropertyName("migration")]
    public DeployPreflightMigration? Migration { get; init; }

    /// <summary>
    /// Database compatibility state for this instance (PostGIS/engine versions).
    /// </summary>
    [JsonPropertyName("databaseCompatibility")]
    public DeployPreflightDatabaseCompatibility? DatabaseCompatibility { get; init; }

    /// <summary>
    /// Cross-plane platform-release projection: the release version each plane currently projects and
    /// whether the serving and geoprocessing worker images are co-versioned (ADR-0060 WS2).
    /// </summary>
    [JsonPropertyName("platformRelease")]
    public DeployPreflightPlatformRelease? PlatformRelease { get; init; }
}

/// <summary>
/// Cross-plane platform-release consistency summary embedded in deploy preflight responses. Lets a
/// mid-upgrade operator see whether the serving deploy targets and the geoprocessing worker images
/// are aligned on the same declared release version.
/// </summary>
public sealed class DeployPreflightPlatformRelease
{
    /// <summary>
    /// Declared platform release version, or null when no release is configured.
    /// </summary>
    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    /// <summary>
    /// Whether a platform release is declared for this instance.
    /// </summary>
    [JsonPropertyName("releaseDeclared")]
    public bool ReleaseDeclared { get; init; }

    /// <summary>
    /// Whether both planes are aligned on the declared release (no element pins a diverging artifact).
    /// </summary>
    [JsonPropertyName("isCoVersioned")]
    public bool IsCoVersioned { get; init; }

    /// <summary>
    /// Serving-plane projections (one per deploy target).
    /// </summary>
    [JsonPropertyName("serving")]
    public IReadOnlyList<DeployPreflightPlaneProjection> Serving { get; init; } = Array.Empty<DeployPreflightPlaneProjection>();

    /// <summary>
    /// Execution-plane projections (one per geoprocessing workload).
    /// </summary>
    [JsonPropertyName("execution")]
    public IReadOnlyList<DeployPreflightPlaneProjection> Execution { get; init; } = Array.Empty<DeployPreflightPlaneProjection>();

    /// <summary>
    /// Identifiers of plane elements pinning an artifact that diverges from the release projection.
    /// </summary>
    [JsonPropertyName("skewedIds")]
    public IReadOnlyList<string> SkewedIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One plane element's platform-release projection embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightPlaneProjection
{
    /// <summary>
    /// Stable identifier of the plane element (deploy target id or execution workload id).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Runtime profile of the element, if any.
    /// </summary>
    [JsonPropertyName("runtimeProfile")]
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Effective artifact reference after applying explicit-pin-wins precedence over the release.
    /// </summary>
    [JsonPropertyName("effectiveArtifactReference")]
    public string? EffectiveArtifactReference { get; init; }

    /// <summary>
    /// Whether the effective artifact was projected from the release rather than an explicit pin.
    /// </summary>
    [JsonPropertyName("projectedFromRelease")]
    public bool ProjectedFromRelease { get; init; }

    /// <summary>
    /// Whether the element pins an explicit artifact that diverges from the release projection.
    /// </summary>
    [JsonPropertyName("skewed")]
    public bool Skewed { get; init; }
}

/// <summary>
/// Readiness summary embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightReadiness
{
    /// <summary>
    /// Whether the instance is currently ready to accept traffic.
    /// </summary>
    [JsonPropertyName("isReady")]
    public bool IsReady { get; init; }

    /// <summary>
    /// HTTP status code that would be returned by the readiness endpoint.
    /// </summary>
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    /// <summary>
    /// Human-readable readiness message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Migration and schema alignment summary embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightMigration
{
    /// <summary>
    /// Current migration lifecycle status observed by this instance.
    /// </summary>
    [JsonPropertyName("lifecycleStatus")]
    public string LifecycleStatus { get; init; } = string.Empty;

    /// <summary>
    /// Optional operator-facing lifecycle detail.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Whether a migration plan could be generated successfully.
    /// </summary>
    [JsonPropertyName("planAvailable")]
    public bool PlanAvailable { get; init; }

    /// <summary>
    /// Whether the current instance detects pending migration scripts.
    /// </summary>
    [JsonPropertyName("upgradeRequired")]
    public bool UpgradeRequired { get; init; }

    /// <summary>
    /// Pending migration scripts for this instance and current database.
    /// </summary>
    [JsonPropertyName("pendingScripts")]
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Scripts previously executed against the database but no longer discovered by the current binary.
    /// </summary>
    [JsonPropertyName("executedButNotDiscoveredScripts")]
    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error detail when migration planning could not be completed.
    /// </summary>
    [JsonPropertyName("planError")]
    public string? PlanError { get; init; }
}

/// <summary>
/// Database compatibility summary embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightDatabaseCompatibility
{
    /// <summary>
    /// Whether the database meets Honua compatibility requirements.
    /// </summary>
    [JsonPropertyName("isCompatible")]
    public bool IsCompatible { get; init; }

    /// <summary>
    /// Database engine version string.
    /// </summary>
    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; init; } = string.Empty;

    /// <summary>
    /// PostGIS extension version, if installed.
    /// </summary>
    [JsonPropertyName("postGisVersion")]
    public string? PostGisVersion { get; init; }

    /// <summary>
    /// PostGIS raster extension version, if installed.
    /// </summary>
    [JsonPropertyName("postGisRasterVersion")]
    public string? PostGisRasterVersion { get; init; }

    /// <summary>
    /// Extensions installed in the database.
    /// </summary>
    [JsonPropertyName("installedExtensions")]
    public IReadOnlyList<string> InstalledExtensions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Non-fatal warnings from the compatibility check.
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error message when the compatibility check determined incompatibility.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request payload for planning a deploy workflow operation.
/// </summary>
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

/// <summary>
/// Request payload for creating a durable deploy workflow operation.
/// </summary>
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

/// <summary>
/// Request payload for a deploy rollback operation.
/// </summary>
public sealed class RollbackDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Request payload for converging the running serving targets onto the declared platform release
/// (ADR-0060 WS2). The endpoint takes <b>no version argument</b>: it always converges to the release
/// declared in <c>ControlPlane:PlatformRelease</c>. Only optional operator metadata is accepted.
/// </summary>
public sealed class PlatformReleaseConvergeRequest
{
    /// <summary>
    /// Optional operator reason recorded on every deploy operation the converge creates.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Optional correlation identifier propagated onto the created deploy operations/proposals.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Response payload for a platform-release converge invocation. Lists the per-target outcome of
/// actuating the declared serving release and states that worker images are not deployed here.
/// </summary>
public sealed class PlatformReleaseConvergeResponse
{
    /// <summary>
    /// Declared platform release version the converge targeted.
    /// </summary>
    [JsonPropertyName("releaseVersion")]
    public string ReleaseVersion { get; init; } = string.Empty;

    /// <summary>
    /// Declared serving artifact reference the divergent targets are converged onto.
    /// </summary>
    [JsonPropertyName("servingArtifactReference")]
    public string? ServingArtifactReference { get; init; }

    /// <summary>
    /// Whether the serving plane was already fully converged: no deploy operation or proposal was
    /// created by this call (every target was already-converged or skipped).
    /// </summary>
    [JsonPropertyName("converged")]
    public bool Converged { get; init; }

    /// <summary>
    /// Always <see langword="true"/>: this API deliberately does not deploy geoprocessing worker
    /// images. Workers converge at the next geoprocessing dispatch via
    /// <c>PlatformReleaseProjection.ResolveWorkerArtifact</c>.
    /// </summary>
    [JsonPropertyName("workersDeferred")]
    public bool WorkersDeferred { get; init; } = true;

    /// <summary>
    /// Operator-facing note explaining that worker images are not deployed by converge.
    /// </summary>
    [JsonPropertyName("workerConvergenceNote")]
    public string WorkerConvergenceNote { get; init; } = string.Empty;

    /// <summary>
    /// Per-target converge outcomes.
    /// </summary>
    [JsonPropertyName("targets")]
    public IReadOnlyList<PlatformReleaseConvergeTargetOutcome> Targets { get; init; } = Array.Empty<PlatformReleaseConvergeTargetOutcome>();

    /// <summary>
    /// Timestamp when the converge result was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// One serving deploy target's outcome from a platform-release converge.
/// </summary>
public sealed class PlatformReleaseConvergeTargetOutcome
{
    /// <summary>
    /// Stable deploy target identifier.
    /// </summary>
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    /// <summary>
    /// Converge classification for this target. One of <c>operation-created</c>,
    /// <c>skipped-pinned</c>, <c>already-converged</c>, <c>unknown-treated-divergent</c>, or the
    /// defensive <c>blocked</c> when the guardrail gateway denied the deploy.
    /// </summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// Deploy operation id when the gateway executed the converge deploy directly. For an
    /// approval-mediated route the id is carried on <see cref="ProposalId"/> instead.
    /// </summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    /// <summary>
    /// Proposal id when the guardrail gateway routed the converge deploy to human approval.
    /// </summary>
    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }

    /// <summary>
    /// Last-applied serving revision detected for the target (the <c>DesiredRevision</c> of its most
    /// recent terminal-Succeeded deploy operation), or null when no terminal-Succeeded operation
    /// exists (unknown → treated as divergent).
    /// </summary>
    [JsonPropertyName("lastAppliedRevision")]
    public string? LastAppliedRevision { get; init; }

    /// <summary>
    /// Operator-facing detail for this target's outcome.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Request payload for submitting or approving a planned deploy operation.
/// </summary>
public sealed class SubmitDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Request payload for manually promoting (forcing the cutover of) a deploy operation that is parked
/// awaiting promotion.
/// </summary>
public sealed class PromoteDeployOperationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Response payload for deploy planning.
/// </summary>
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

/// <summary>
/// Deploy target metadata resolved during planning.
/// </summary>
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

/// <summary>
/// Response payload for backend capabilities used by deploy planning.
/// </summary>
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

/// <summary>
/// Response payload for a durable deploy workflow operation.
/// </summary>
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

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Paged, newest-first collection of durable deploy workflow operations.
/// </summary>
public sealed class DeployOperationListResponse
{
    /// <summary>
    /// Operations on this page, ordered newest-first by creation time.
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<DeployOperationResponse> Items { get; init; } = Array.Empty<DeployOperationResponse>();

    /// <summary>
    /// One-based page number that produced this page.
    /// </summary>
    [JsonPropertyName("page")]
    public int Page { get; init; }

    /// <summary>
    /// Effective page size applied when producing this page.
    /// </summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    /// <summary>
    /// Total number of operations matching the filter within the materialized window.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    /// <summary>
    /// Whether at least one more page exists after this one.
    /// </summary>
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// Metadata release lifecycle context embedded in a workflow operation response.
/// </summary>
public sealed class MetadataReleaseContextResponse
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; init; } = string.Empty;

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

/// <summary>
/// Rollback plan embedded in a metadata release operation response.
/// </summary>
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

/// <summary>
/// Metadata release evidence reference embedded in operation responses.
/// </summary>
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
