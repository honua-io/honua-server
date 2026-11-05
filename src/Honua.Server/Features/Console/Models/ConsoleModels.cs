// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Console.Models;

public sealed class CreateConsoleContentItemRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [Required]
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [StringLength(200)]
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus? Lifecycle { get; init; }

    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState? OperationalState { get; init; }

    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef>? Provenance { get; init; }

    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }
}

public sealed class UpdateConsoleContentItemRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    // Item category is immutable on update and must match the stored value.
    [Required]
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [StringLength(200)]
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus? Lifecycle { get; init; }

    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState? OperationalState { get; init; }

    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef>? Provenance { get; init; }

    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

public sealed class PatchConsoleContentItemRequest
{
    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }
}

public sealed class ConsoleContentListResponse
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<ConsoleContentItem> Items { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed class ConsoleActionCheckTarget
{
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }

    [JsonPropertyName("routeKey")]
    public string? RouteKey { get; init; }
}

public sealed class ConsoleActionCheckRequest
{
    [Required]
    [JsonPropertyName("targets")]
    public required IReadOnlyList<ConsoleActionCheckTarget> Targets { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<ConsoleContentAction>? Actions { get; init; }
}

public sealed class ConsoleActionCheckResult
{
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }

    [JsonPropertyName("routeKey")]
    public string? RouteKey { get; init; }

    [JsonPropertyName("allowed")]
    public required IReadOnlyList<ConsoleContentAction> Allowed { get; init; }

    [JsonPropertyName("denied")]
    public required IReadOnlyList<ConsoleContentAction> Denied { get; init; }

    [JsonPropertyName("notFound")]
    public bool NotFound { get; init; }
}

public sealed class ConsoleActionCheckResponse
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<ConsoleActionCheckResult> Results { get; init; }
}

public sealed class ConsoleProvenanceChainResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("chain")]
    public required IReadOnlyList<ConsoleContentItem> Chain { get; init; }

    [JsonPropertyName("maxDepth")]
    public required int MaxDepth { get; init; }
}
