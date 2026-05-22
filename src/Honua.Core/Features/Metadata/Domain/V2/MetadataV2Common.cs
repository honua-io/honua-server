// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Constants for Metadata v2 model documents.
/// </summary>
public static class MetadataV2Constants
{
    /// <summary>
    /// Initial Metadata v2 schema version.
    /// </summary>
    public const string SchemaVersion = "2.0.0-alpha.1";

    /// <summary>
    /// Initial Metadata v2 API version.
    /// </summary>
    public const string ApiVersion = "metadata.honua.io/v2alpha1";
}

/// <summary>
/// Common metadata fields shared by Metadata v2 graph entities.
/// </summary>
public sealed record MetadataV2ObjectMetadata
{
    /// <summary>
    /// Stable identifier within the graph.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Machine-friendly name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional namespace for grouping entities.
    /// </summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>
    /// Human-readable display title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Labels for selection and grouping (Kubernetes-style selectable key/values).
    /// Discovery / search tooling reads these. Express a "tag" as a label with an
    /// empty value: <c>{"public": "", "weather": ""}</c>.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Tooling annotations (Kubernetes-style opaque key/values; not selectable).
    /// </summary>
    [JsonPropertyName("annotations")]
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Entity generation for optimistic concurrency.
    /// </summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    /// <summary>
    /// Timestamp when the entity was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the entity was updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Canonical field description owned by a Metadata v2 resource.
/// </summary>
public sealed record MetadataV2Field
{
    /// <summary>
    /// Stable source field name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Canonical field type. String-encoded in JSON for snapshot readability.
    /// </summary>
    [JsonPropertyName("type")]
    public MetadataV2FieldType Type { get; init; } = MetadataV2FieldType.Unknown;

    /// <summary>
    /// Human-readable field title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable field description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// True when null values are valid for the field.
    /// </summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    /// <summary>
    /// Semantic role identifiers used by catalog and service projections.
    /// </summary>
    [JsonPropertyName("semanticRoles")]
    public IReadOnlyList<string> SemanticRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extension data for the field.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}
