// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.OgcProcesses.Models;

/// <summary>
/// OGC API Processes process summary returned in process list responses.
/// </summary>
public sealed record OgcProcessSummary
{
    /// <summary>
    /// Process identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title for the process.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the process.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Process version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Supported job control options (sync-execute, async-execute, dismiss).
    /// </summary>
    [JsonPropertyName("jobControlOptions")]
    public ImmutableArray<string>? JobControlOptions { get; init; }

    /// <summary>
    /// Supported output transmission modes.
    /// </summary>
    [JsonPropertyName("outputTransmission")]
    public ImmutableArray<string>? OutputTransmission { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

/// <summary>
/// OGC API Processes process list response.
/// </summary>
public sealed record OgcProcessList
{
    /// <summary>
    /// List of available processes.
    /// </summary>
    [JsonPropertyName("processes")]
    public required ImmutableArray<OgcProcessSummary> Processes { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

/// <summary>
/// OGC API Processes input/output description using JSON Schema.
/// </summary>
public sealed record OgcProcessIoDescription
{
    /// <summary>
    /// Human-readable title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the input or output.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// JSON Schema describing the value.
    /// </summary>
    [JsonPropertyName("schema")]
    public required OgcProcessIoSchema Schema { get; init; }
}

/// <summary>
/// Minimal JSON Schema fragment for process input/output descriptions.
/// </summary>
public sealed record OgcProcessIoSchema
{
    /// <summary>
    /// JSON Schema type (string, number, object, array, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Content media type hint for complex inputs.
    /// </summary>
    [JsonPropertyName("contentMediaType")]
    public string? ContentMediaType { get; init; }
}

/// <summary>
/// Full OGC API Processes process description.
/// </summary>
public sealed record OgcProcessDescription
{
    /// <summary>
    /// Process identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title for the process.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the process.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Process version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Supported job control options.
    /// </summary>
    [JsonPropertyName("jobControlOptions")]
    public ImmutableArray<string>? JobControlOptions { get; init; }

    /// <summary>
    /// Supported output transmission modes.
    /// </summary>
    [JsonPropertyName("outputTransmission")]
    public ImmutableArray<string>? OutputTransmission { get; init; }

    /// <summary>
    /// Input descriptions keyed by input identifier.
    /// </summary>
    [JsonPropertyName("inputs")]
    public ImmutableDictionary<string, OgcProcessIoDescription>? Inputs { get; init; }

    /// <summary>
    /// Output descriptions keyed by output identifier.
    /// </summary>
    [JsonPropertyName("outputs")]
    public ImmutableDictionary<string, OgcProcessIoDescription>? Outputs { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

/// <summary>
/// OGC API Processes execution request body.
/// </summary>
public sealed record OgcExecuteRequest
{
    /// <summary>
    /// Process inputs keyed by input identifier.
    /// </summary>
    [JsonPropertyName("inputs")]
    public ImmutableDictionary<string, string>? Inputs { get; init; }

    /// <summary>
    /// Desired response mode (document or raw). V1 supports document only.
    /// </summary>
    [JsonPropertyName("response")]
    public string? Response { get; init; }
}

/// <summary>
/// OGC API Processes job status info (StatusInfo schema).
/// </summary>
public sealed record OgcStatusInfo
{
    /// <summary>
    /// Process identifier that created this job.
    /// </summary>
    [JsonPropertyName("processID")]
    public string? ProcessId { get; init; }

    /// <summary>
    /// Resource type discriminator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "process";

    /// <summary>
    /// Job identifier.
    /// </summary>
    [JsonPropertyName("jobID")]
    public required string JobId { get; init; }

    /// <summary>
    /// Current OGC job status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Job creation timestamp (RFC 3339).
    /// </summary>
    [JsonPropertyName("created")]
    public string? Created { get; init; }

    /// <summary>
    /// Last update timestamp (RFC 3339).
    /// </summary>
    [JsonPropertyName("updated")]
    public string? Updated { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    [JsonPropertyName("progress")]
    public int? Progress { get; init; }

    /// <summary>
    /// Links to related resources.
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Ogc.Common.Link>? Links { get; init; }
}

/// <summary>
/// OGC API Processes exception response (RFC 7807).
/// </summary>
public sealed record OgcProcessError
{
    /// <summary>
    /// Error type URI.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Short human-readable summary.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// HTTP status code.
    /// </summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>
    /// Detailed human-readable explanation.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>
    /// URI identifying the specific occurrence.
    /// </summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }
}

/// <summary>
/// OGC API Processes document-mode results response.
/// Keys are stable output identifiers, values are the output payloads.
/// </summary>
public sealed record OgcResultsDocument
{
    /// <summary>
    /// Output values keyed by stable output identifier.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Outputs { get; init; }
}
