// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.GeoETL.Models;

/// <summary>
/// Wire representation of a connector configuration (source or sink).
/// </summary>
public sealed record ConnectorConfigDto
{
    /// <summary>Connector type discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>Connector-specific options.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Wire representation of a transform configuration.
/// </summary>
public sealed record TransformConfigDto
{
    /// <summary>Transform type discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>Transform-specific options.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Wire representation of a single pipeline stage.
/// </summary>
public sealed record PipelineStageDto
{
    /// <summary>Stage kind: <c>source</c>, <c>transform</c>, or <c>sink</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Connector configuration for source / sink stages.</summary>
    [JsonPropertyName("connector")]
    public ConnectorConfigDto? Connector { get; init; }

    /// <summary>Transform configuration for transform stages.</summary>
    [JsonPropertyName("transform")]
    public TransformConfigDto? Transform { get; init; }
}

/// <summary>
/// Request body for creating or updating a pipeline definition.
/// </summary>
public sealed record PipelineDefinitionRequest
{
    /// <summary>Schema version of the definition document.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Human-readable pipeline name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Optional description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Ordered stage chain.</summary>
    [JsonPropertyName("stages")]
    public List<PipelineStageDto> Stages { get; init; } = [];
}

/// <summary>
/// Response body for a pipeline definition.
/// </summary>
public sealed record PipelineDefinitionResponse
{
    /// <summary>Schema version.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    /// <summary>Pipeline id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>Pipeline name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Definition version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>Stage chain.</summary>
    [JsonPropertyName("stages")]
    public List<PipelineStageDto> Stages { get; init; } = [];

    /// <summary>Creation timestamp.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Advisory validation warnings (non-blocking), for example native-worker requirements.</summary>
    [JsonPropertyName("advisories")]
    public List<string> Advisories { get; init; } = [];
}

/// <summary>
/// Response body listing pipeline definitions.
/// </summary>
public sealed record PipelineListResponse
{
    /// <summary>The pipeline definitions.</summary>
    [JsonPropertyName("pipelines")]
    public List<PipelineDefinitionResponse> Pipelines { get; init; } = [];
}

/// <summary>
/// Response body for a pipeline execution record.
/// </summary>
public sealed record PipelineExecutionResponse
{
    /// <summary>Execution id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>Pipeline id.</summary>
    [JsonPropertyName("pipeline_id")]
    public string PipelineId { get; init; } = "";

    /// <summary>Pipeline definition version executed.</summary>
    [JsonPropertyName("pipeline_version")]
    public int PipelineVersion { get; init; }

    /// <summary>Substrate execution job id.</summary>
    [JsonPropertyName("execution_job_id")]
    public string ExecutionJobId { get; init; } = "";

    /// <summary>Execution status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>Whether this was a dry run.</summary>
    [JsonPropertyName("is_dry_run")]
    public bool IsDryRun { get; init; }

    /// <summary>Features read from the source.</summary>
    [JsonPropertyName("features_read")]
    public long FeaturesRead { get; init; }

    /// <summary>Features written by the sink.</summary>
    [JsonPropertyName("features_written")]
    public long FeaturesWritten { get; init; }

    /// <summary>Features routed to quarantine.</summary>
    [JsonPropertyName("features_quarantined")]
    public long FeaturesQuarantined { get; init; }

    /// <summary>Soft-delete rollback batch id.</summary>
    [JsonPropertyName("batch_id")]
    public string? BatchId { get; init; }

    /// <summary>Error message when failed.</summary>
    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    /// <summary>Creation timestamp.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Terminal timestamp, when complete.</summary>
    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Response body listing pipeline executions.
/// </summary>
public sealed record PipelineExecutionListResponse
{
    /// <summary>The execution records.</summary>
    [JsonPropertyName("executions")]
    public List<PipelineExecutionResponse> Executions { get; init; } = [];
}
