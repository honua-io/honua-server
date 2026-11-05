// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

// -----------------------------------------------------------------------
// Resource response shapes
// -----------------------------------------------------------------------

internal sealed class McpProposalResource
{
    [JsonPropertyName("proposalId")]
    public string ProposalId { get; set; } = string.Empty;

    [JsonPropertyName("operationInstanceId")]
    public string? OperationInstanceId { get; set; }

    [JsonPropertyName("auditId")]
    public string? AuditId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("riskLevel")]
    public string RiskLevel { get; set; } = string.Empty;

    [JsonPropertyName("diff")]
    public IReadOnlyList<string> Diff { get; set; } = [];

    [JsonPropertyName("dryRun")]
    public IReadOnlyList<string> DryRun { get; set; } = [];

    [JsonPropertyName("blockingReasons")]
    public IReadOnlyList<string> BlockingReasons { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("resolvedBy")]
    public string? ResolvedBy { get; set; }

    [JsonPropertyName("resolutionReason")]
    public string? ResolutionReason { get; set; }

    [JsonPropertyName("executionOperationId")]
    public string? ExecutionOperationId { get; set; }

    [JsonPropertyName("resourceIds")]
    public IReadOnlyDictionary<string, string>? ResourceIds { get; set; }

    [JsonPropertyName("publicationId")]
    public string? PublicationId { get; set; }

    [JsonPropertyName("activeUrl")]
    public string? ActiveUrl { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class McpJobResource
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; set; }

    [JsonPropertyName("currentPhase")]
    public string? CurrentPhase { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("resultsUri")]
    public string? ResultsUri { get; set; }
}

internal sealed class McpJobResultsResource
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("resultPackageId")]
    public string ResultPackageId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public McpResultSummary Summary { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<McpArtifactRef> Artifacts { get; set; } = [];

    [JsonPropertyName("totalArtifactCount")]
    public int TotalArtifactCount { get; set; }

    [JsonPropertyName("artifactsTruncated")]
    public bool ArtifactsTruncated { get; set; }

    [JsonPropertyName("workspaceRefs")]
    public IReadOnlyList<McpWorkspaceRef> WorkspaceRefs { get; set; } = [];

    [JsonPropertyName("mapPackageId")]
    public string? MapPackageId { get; set; }

    [JsonPropertyName("appPackageId")]
    public string? AppPackageId { get; set; }

    [JsonPropertyName("assumptions")]
    public IReadOnlyList<string> Assumptions { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpProvenance Provenance { get; set; } = new();

    [JsonPropertyName("errors")]
    public IReadOnlyList<McpGeoprocessingError> Errors { get; set; } = [];

    [JsonPropertyName("notImplementedReason")]
    public string? NotImplementedReason { get; set; }
}

internal sealed class McpResultSummary
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class McpArtifactRef
{
    [JsonPropertyName("artifactId")]
    public string ArtifactId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}

internal sealed class McpWorkspaceRef
{
    [JsonPropertyName("workspaceId")]
    public string WorkspaceId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }
}

internal sealed class McpProvenance
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<McpProvenanceSource> Sources { get; set; } = [];

    [JsonPropertyName("processDefinitions")]
    public IReadOnlyList<string> ProcessDefinitions { get; set; } = [];

    [JsonPropertyName("assumptions")]
    public IReadOnlyList<string> Assumptions { get; set; } = [];

    [JsonPropertyName("clarificationsAsked")]
    public IReadOnlyList<string> ClarificationsAsked { get; set; } = [];

    [JsonPropertyName("clarificationsAnswered")]
    public IReadOnlyList<string> ClarificationsAnswered { get; set; } = [];

    [JsonPropertyName("executedAt")]
    public DateTimeOffset? ExecutedAt { get; set; }

    [JsonPropertyName("generatedArtifactIds")]
    public IReadOnlyList<string> GeneratedArtifactIds { get; set; } = [];
}

internal sealed class McpProvenanceSource
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class McpGeoprocessingError
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("stepId")]
    public string? StepId { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation>? Violations { get; set; }
}

internal sealed class McpWorkspaceResource
{
    [JsonPropertyName("workspaceId")]
    public string WorkspaceId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("lifecycleState")]
    public string? LifecycleState { get; set; }

    [JsonPropertyName("cleanupScheduledAt")]
    public DateTimeOffset? CleanupScheduledAt { get; set; }

    [JsonPropertyName("resultsUri")]
    public string? ResultsUri { get; set; }

    [JsonPropertyName("notImplementedReason")]
    public string? NotImplementedReason { get; set; }
}

internal sealed class McpProcessCatalogResource
{
    [JsonPropertyName("catalogVersion")]
    public string CatalogVersion { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("processes")]
    public IReadOnlyList<McpProcessEntry> Processes { get; set; } = [];

    [JsonPropertyName("notImplementedReason")]
    public string? NotImplementedReason { get; set; }
}

internal sealed class McpProcessEntry
{
    [JsonPropertyName("processId")]
    public string ProcessId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public IReadOnlyList<McpProcessParameter> Parameters { get; set; } = [];

    [JsonPropertyName("outputArtifactKinds")]
    public IReadOnlyList<string> OutputArtifactKinds { get; set; } = [];

    [JsonPropertyName("runtimeProfile")]
    public string RuntimeProfile { get; set; } = string.Empty;

    [JsonPropertyName("executionKind")]
    public string ExecutionKind { get; set; } = string.Empty;

    [JsonPropertyName("entryPoints")]
    public IReadOnlyList<string> EntryPoints { get; set; } = [];

    [JsonPropertyName("supportedExecutionModes")]
    public IReadOnlyList<string> SupportedExecutionModes { get; set; } = [];

    [JsonPropertyName("configurationDependency")]
    public string? ConfigurationDependency { get; set; }

    [JsonPropertyName("executionCapabilityReason")]
    public string? ExecutionCapabilityReason { get; set; }
}

internal sealed class McpProcessParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("valueType")]
    public string ValueType { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("allowedValues")]
    public IReadOnlyList<string>? AllowedValues { get; set; }
}

// -----------------------------------------------------------------------
// Promotion-surface resources (published services, deployments, packages)
// -----------------------------------------------------------------------

internal sealed class McpHostedProvenance
{
    [JsonPropertyName("originatingIntentId")]
    public string? OriginatingIntentId { get; set; }

    [JsonPropertyName("resultPackageId")]
    public string? ResultPackageId { get; set; }

    [JsonPropertyName("jobResourceUri")]
    public string? JobResourceUri { get; set; }

    [JsonPropertyName("jobResultsResourceUri")]
    public string? JobResultsResourceUri { get; set; }

    [JsonPropertyName("publishedServiceResourceUri")]
    public string? PublishedServiceResourceUri { get; set; }

    [JsonPropertyName("supersededByDeploymentResourceUri")]
    public string? SupersededByDeploymentResourceUri { get; set; }
}

internal sealed class McpPublishedServiceView
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("metadataServiceId")]
    public string? MetadataServiceId { get; set; }

    [JsonPropertyName("layerIds")]
    public IReadOnlyList<int>? LayerIds { get; set; }

    [JsonPropertyName("servingLayerIds")]
    public IReadOnlyList<int>? ServingLayerIds { get; set; }

    [JsonPropertyName("featureServerEnabled")]
    public bool? FeatureServerEnabled { get; set; }

    [JsonPropertyName("serving")]
    public bool? Serving { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("lastRefreshedAt")]
    public DateTimeOffset? LastRefreshedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<McpArtifactRef> Artifacts { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("deploymentCount")]
    public int DeploymentCount { get; set; }

    [JsonPropertyName("deploymentResourceUris")]
    public IReadOnlyList<string> DeploymentResourceUris { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpHostedProvenance Provenance { get; set; } = new();
}

internal sealed class McpPublishedServiceSummary
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;
}

internal sealed class McpPublishedServiceListView
{
    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<McpPublishedServiceSummary> Items { get; set; } = [];
}

internal sealed class McpDeploymentTransitionView
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("rolloutState")]
    public string? RolloutState { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class McpDeploymentView
{
    [JsonPropertyName("deploymentId")]
    public string DeploymentId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("publicationState")]
    public string PublicationState { get; set; } = string.Empty;

    [JsonPropertyName("rolloutState")]
    public string RolloutState { get; set; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("sourceResourceUri")]
    public string? SourceResourceUri { get; set; }

    [JsonPropertyName("studioItemId")]
    public Guid? StudioItemId { get; set; }

    [JsonPropertyName("studioVersionId")]
    public Guid? StudioVersionId { get; set; }

    [JsonPropertyName("studioContentHash")]
    public string? StudioContentHash { get; set; }

    [JsonPropertyName("studioOwnerId")]
    public string? StudioOwnerId { get; set; }

    [JsonPropertyName("studioPublicationRequestId")]
    public Guid? StudioPublicationRequestId { get; set; }

    [JsonPropertyName("studioRollbackRequestId")]
    public Guid? StudioRollbackRequestId { get; set; }

    [JsonPropertyName("studioFamily")]
    public string? StudioFamily { get; set; }

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("hostingMode")]
    public string HostingMode { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("routePrefix")]
    public string? RoutePrefix { get; set; }

    [JsonPropertyName("publicUrl")]
    public string? PublicUrl { get; set; }

    [JsonPropertyName("runtimeHealth")]
    public string RuntimeHealth { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("activatedAt")]
    public DateTimeOffset? ActivatedAt { get; set; }

    [JsonPropertyName("retiredAt")]
    public DateTimeOffset? RetiredAt { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;

    [JsonPropertyName("transitions")]
    public IReadOnlyList<McpDeploymentTransitionView> Transitions { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpHostedProvenance Provenance { get; set; } = new();
}

internal sealed class McpDeploymentSummary
{
    [JsonPropertyName("deploymentId")]
    public string DeploymentId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("publicationState")]
    public string PublicationState { get; set; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;
}

internal sealed class McpDeploymentListView
{
    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<McpDeploymentSummary> Items { get; set; } = [];
}

internal sealed class McpPackageView
{
    [JsonPropertyName("packageKind")]
    public string PackageKind { get; set; } = string.Empty;

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("packageStatus")]
    public string PackageStatus { get; set; } = string.Empty;

    [JsonPropertyName("deploymentCount")]
    public int DeploymentCount { get; set; }

    [JsonPropertyName("deploymentResourceUris")]
    public IReadOnlyList<string> DeploymentResourceUris { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpHostedProvenance Provenance { get; set; } = new();
}

internal sealed class McpPackageSummary
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("deploymentCount")]
    public int DeploymentCount { get; set; }
}

internal sealed class McpPackageListView
{
    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("packageKind")]
    public string PackageKind { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<McpPackageSummary> Items { get; set; } = [];
}
