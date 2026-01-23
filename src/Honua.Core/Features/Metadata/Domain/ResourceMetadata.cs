// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Metadata fields common to all resources, including identity and versioning.
/// </summary>
public sealed record ResourceMetadata
{
    /// <summary>
    /// Stable resource identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Human-readable name within the namespace.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Namespace for grouping resources.
    /// </summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>
    /// Key-value labels for selection and grouping.
    /// </summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Free-form annotations for tooling and audit metadata.
    /// </summary>
    [JsonPropertyName("annotations")]
    public Dictionary<string, string>? Annotations { get; init; }

    /// <summary>
    /// Monotonic resource version for optimistic concurrency.
    /// </summary>
    [JsonPropertyName("resourceVersion")]
    public string? ResourceVersion { get; init; }

    /// <summary>
    /// Generation number that increments when spec changes.
    /// </summary>
    [JsonPropertyName("generation")]
    public int? Generation { get; init; }

    /// <summary>
    /// Timestamp when the resource was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the resource was last updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }
}
