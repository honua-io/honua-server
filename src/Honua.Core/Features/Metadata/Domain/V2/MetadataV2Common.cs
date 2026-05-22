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
/// Lifecycle and observed status shared by Metadata v2 graph entities.
/// </summary>
public sealed record MetadataV2Status
{
    /// <summary>
    /// Desired or declared lifecycle state.
    /// </summary>
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus Lifecycle { get; init; } = MetadataV2LifecycleStatus.Draft;

    /// <summary>
    /// Observed operational state.
    /// </summary>
    [JsonPropertyName("state")]
    public MetadataV2OperationalState State { get; init; } = MetadataV2OperationalState.Unknown;

    /// <summary>
    /// Reconciliation or validation conditions.
    /// </summary>
    [JsonPropertyName("conditions")]
    public IReadOnlyList<MetadataV2Condition> Conditions { get; init; } = Array.Empty<MetadataV2Condition>();

    /// <summary>
    /// Last observed timestamp.
    /// </summary>
    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }
}

/// <summary>
/// A status condition attached to a Metadata v2 entity.
/// </summary>
public sealed record MetadataV2Condition
{
    /// <summary>
    /// Condition type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Condition status.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Machine-readable reason.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Last transition timestamp.
    /// </summary>
    [JsonPropertyName("lastTransitionAt")]
    public DateTimeOffset? LastTransitionAt { get; init; }
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
