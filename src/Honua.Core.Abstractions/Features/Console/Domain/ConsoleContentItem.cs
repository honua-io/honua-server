// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Canonical content item surfaced through the Console catalog API.
/// </summary>
/// <remarks>
/// The Console slice owns this wire shape so the Metadata v2 graph model can
/// evolve independently of Console API versioning. Field naming follows the
/// <see cref="MetadataV2ObjectMetadata"/> conventions to keep client mental
/// models consistent.
/// </remarks>
public sealed record ConsoleContentItem
{
    /// <summary>
    /// Stable identifier within the Console catalog.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Machine-friendly name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Optional namespace for grouping items.
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
    /// Item category — drives shape of <see cref="TypeMetadata"/> and downstream
    /// rendering decisions.
    /// </summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>
    /// Tags for discovery.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Labels for selection and grouping.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Lifecycle status reused from the Metadata v2 enum set.
    /// </summary>
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus Lifecycle { get; init; } = MetadataV2LifecycleStatus.Draft;

    /// <summary>
    /// Observed operational state reused from the Metadata v2 enum set.
    /// </summary>
    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState OperationalState { get; init; } = MetadataV2OperationalState.Unknown;

    /// <summary>
    /// Sharing scope for the item.
    /// </summary>
    [JsonPropertyName("visibility")]
    public ConsoleVisibility Visibility { get; init; } = ConsoleVisibility.Personal;

    /// <summary>
    /// Owner principal id.
    /// </summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>
    /// Optional team/group scope id (paired with <see cref="ConsoleVisibility.Team"/>).
    /// </summary>
    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    /// <summary>
    /// Identifier of the principal that originally created the item.
    /// </summary>
    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    /// <summary>
    /// Identifier of the principal that last updated the item.
    /// </summary>
    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }

    /// <summary>
    /// Timestamp when the item was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the item was last updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Entity generation for optimistic concurrency. Incremented on full PUT.
    /// </summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    /// <summary>
    /// Verbs the requesting principal may perform on this item. Server-authored —
    /// never stored, always computed before the item leaves the API boundary.
    /// </summary>
    [JsonPropertyName("actions")]
    public IReadOnlyList<ConsoleContentAction> Actions { get; init; } = Array.Empty<ConsoleContentAction>();

    /// <summary>
    /// Provenance references to related artifacts.
    /// </summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>
    /// Type-specific metadata sidecar. Shape depends on <see cref="ItemType"/>.
    /// Serialized as an opaque JSON element to stay AOT-safe; clients decode it
    /// using typed helpers keyed on <see cref="ItemType"/>.
    /// </summary>
    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }
}
