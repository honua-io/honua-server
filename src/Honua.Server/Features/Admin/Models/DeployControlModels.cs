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
/// Request payload for submitting or approving a planned deploy operation.
/// </summary>
public sealed class SubmitDeployOperationRequest
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
