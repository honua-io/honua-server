// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Console.Domain;

// The Console slice owns this wire shape so the Metadata v2 graph can change without changing the Console API version.
public sealed record ConsoleContentItem
{
    [JsonIgnore]
    public bool IsReadOnly { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus Lifecycle { get; init; } = MetadataV2LifecycleStatus.Draft;

    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState OperationalState { get; init; } = MetadataV2OperationalState.Unknown;

    [JsonPropertyName("visibility")]
    public ConsoleVisibility Visibility { get; init; } = ConsoleVisibility.Personal;

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    // Permitted verbs are computed before the item leaves the API and are never stored.
    [JsonPropertyName("actions")]
    public IReadOnlyList<ConsoleContentAction> Actions { get; init; } = Array.Empty<ConsoleContentAction>();

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    // The sidecar is an opaque JSON element so the model stays AOT-safe.
    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }
}
