// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mcp.Models;

// -----------------------------------------------------------------------
// Resource response shapes
// -----------------------------------------------------------------------

/// <summary>
/// Response body for reads of <c>honua://jobs/{jobId}</c>.
/// </summary>
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

/// <summary>
/// Response body for reads of <c>honua://jobs/{jobId}/results</c>. Mirrors the
/// canonical <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisResultPackage"/>
/// plus the addressing <c>jobId</c> carried in the MCP URI.
/// </summary>
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

/// <summary>
/// Response body for reads of <c>honua://workspaces/{workspaceId}</c>.
/// </summary>
internal sealed class McpWorkspaceResource
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

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("notImplementedReason")]
    public string? NotImplementedReason { get; set; }
}

/// <summary>
/// Response body for reads of <c>honua://catalog/processes</c>.
/// </summary>
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

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

// -----------------------------------------------------------------------
// Promotion-surface resources (published services, deployments, packages)
// -----------------------------------------------------------------------

/// <summary>
/// Provenance edges carried by promotion-surface resources so agents can trace a hosted
/// surface back to the workflow that produced it. Distinct from <see cref="McpProvenance"/>,
/// which covers analysis-result provenance (sources, process definitions, clarifications).
/// </summary>
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

    [JsonPropertyName("parentDeploymentResourceUri")]
    public string? ParentDeploymentResourceUri { get; set; }

    [JsonPropertyName("supersededByDeploymentResourceUri")]
    public string? SupersededByDeploymentResourceUri { get; set; }
}

/// <summary>
/// Response body for reads of <c>honua://published-services/{serviceId}</c>.
/// </summary>
internal sealed class McpPublishedServiceView
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

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
    public DateTimeOffset PublishedAt { get; set; }

    [JsonPropertyName("lastRefreshedAt")]
    public DateTimeOffset? LastRefreshedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<McpArtifactRef> Artifacts { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpHostedProvenance Provenance { get; set; } = new();
}

/// <summary>
/// Summary entry used in <c>honua://published-services</c> list envelopes.
/// </summary>
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
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;
}

/// <summary>
/// Response body for reads of <c>honua://published-services</c>.
/// </summary>
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

/// <summary>
/// Lifecycle transition exposed on <c>honua://deployments/{id}</c> reads.
/// </summary>
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

/// <summary>
/// Response body for reads of <c>honua://deployments/{deploymentId}</c>.
/// </summary>
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

/// <summary>
/// Summary entry used in <c>honua://deployments</c> list envelopes.
/// </summary>
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

/// <summary>
/// Response body for reads of <c>honua://deployments</c>.
/// </summary>
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

/// <summary>
/// Response body for reads of <c>honua://map-packages/{packageId}</c> and
/// <c>honua://app-packages/{packageId}</c>. Map and app packages do not have a
/// standalone store; the resource derives its visibility from the deployments
/// that reference the package, so <see cref="DeploymentResourceUris"/> is the
/// canonical way to reach hosted surfaces that serve the package.
/// </summary>
internal sealed class McpPackageView
{
    [JsonPropertyName("packageKind")]
    public string PackageKind { get; set; } = string.Empty;

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("deploymentCount")]
    public int DeploymentCount { get; set; }

    [JsonPropertyName("deploymentResourceUris")]
    public IReadOnlyList<string> DeploymentResourceUris { get; set; } = [];

    [JsonPropertyName("provenance")]
    public McpHostedProvenance Provenance { get; set; } = new();
}

/// <summary>
/// Summary entry used in <c>honua://map-packages</c> and
/// <c>honua://app-packages</c> list envelopes.
/// </summary>
internal sealed class McpPackageSummary
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("deploymentCount")]
    public int DeploymentCount { get; set; }
}

/// <summary>
/// Response body for reads of <c>honua://map-packages</c> and
/// <c>honua://app-packages</c>.
/// </summary>
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
