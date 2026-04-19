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
/// Response body for reads of <c>honua://jobs/{jobId}/results</c>.
/// </summary>
internal sealed class McpJobResultsResource
{
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
