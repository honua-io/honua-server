// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// Request body for creating a Console content item.
/// </summary>
public sealed class CreateConsoleContentItemRequest
{
    /// <summary>Machine-friendly name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Item category.</summary>
    [Required]
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Optional namespace.</summary>
    [StringLength(200)]
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Display title.</summary>
    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Description.</summary>
    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Labels.</summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Lifecycle.</summary>
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus? Lifecycle { get; init; }

    /// <summary>Operational state.</summary>
    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState? OperationalState { get; init; }

    /// <summary>Visibility scope.</summary>
    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }

    /// <summary>Owner principal id.</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Team scope id (paired with <c>team</c> visibility).</summary>
    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef>? Provenance { get; init; }

    /// <summary>Type-specific sidecar.</summary>
    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }
}

/// <summary>
/// Request body for replacing a content item (full PUT).
/// </summary>
public sealed class UpdateConsoleContentItemRequest
{
    /// <summary>New name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Item category (immutable on update — must match the stored value).</summary>
    [Required]
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Namespace.</summary>
    [StringLength(200)]
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Title.</summary>
    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Description.</summary>
    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Labels.</summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Lifecycle.</summary>
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus? Lifecycle { get; init; }

    /// <summary>Operational state.</summary>
    [JsonPropertyName("operationalState")]
    public MetadataV2OperationalState? OperationalState { get; init; }

    /// <summary>Visibility.</summary>
    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }

    /// <summary>Owner principal id.</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Team scope id.</summary>
    [JsonPropertyName("teamScopeId")]
    public string? TeamScopeId { get; init; }

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef>? Provenance { get; init; }

    /// <summary>Type-specific sidecar.</summary>
    [JsonPropertyName("typeMetadata")]
    public JsonElement? TypeMetadata { get; init; }

    /// <summary>
    /// Expected generation for optimistic concurrency. When supplied, the
    /// server rejects the update unless it exactly matches the stored value;
    /// both older and newer generations produce <c>409 Conflict</c>. Omit to
    /// bypass the check (last-write-wins).
    /// </summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

/// <summary>
/// Request body for partially updating displayable item fields.
/// </summary>
public sealed class PatchConsoleContentItemRequest
{
    /// <summary>Title.</summary>
    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Description.</summary>
    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Labels.</summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Visibility.</summary>
    [JsonPropertyName("visibility")]
    public ConsoleVisibility? Visibility { get; init; }
}

/// <summary>
/// Paginated content list response payload.
/// </summary>
public sealed class ConsoleContentListResponse
{
    /// <summary>Items in this page.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<ConsoleContentItem> Items { get; init; }

    /// <summary>Total matches across all pages.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary>Cursor for the next page, or null.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Per-target slot in an action-check request.
/// </summary>
public sealed class ConsoleActionCheckTarget
{
    /// <summary>Content item id to evaluate.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }

    /// <summary>Navigation route key to evaluate.</summary>
    [JsonPropertyName("routeKey")]
    public string? RouteKey { get; init; }
}

/// <summary>
/// Request body for the action-check endpoint.
/// </summary>
public sealed class ConsoleActionCheckRequest
{
    /// <summary>Targets to evaluate.</summary>
    [Required]
    [JsonPropertyName("targets")]
    public required IReadOnlyList<ConsoleActionCheckTarget> Targets { get; init; }

    /// <summary>
    /// Actions to evaluate. When omitted, all known actions are evaluated.
    /// </summary>
    [JsonPropertyName("actions")]
    public IReadOnlyList<ConsoleContentAction>? Actions { get; init; }
}

/// <summary>
/// One evaluated target in an action-check response.
/// </summary>
public sealed class ConsoleActionCheckResult
{
    /// <summary>Item id, when the target was an item.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }

    /// <summary>Route key, when the target was a route.</summary>
    [JsonPropertyName("routeKey")]
    public string? RouteKey { get; init; }

    /// <summary>Actions the principal is permitted to perform.</summary>
    [JsonPropertyName("allowed")]
    public required IReadOnlyList<ConsoleContentAction> Allowed { get; init; }

    /// <summary>Actions the principal is denied.</summary>
    [JsonPropertyName("denied")]
    public required IReadOnlyList<ConsoleContentAction> Denied { get; init; }

    /// <summary>
    /// True when the target id/route was unknown; allowed/denied are empty in
    /// this case.
    /// </summary>
    [JsonPropertyName("notFound")]
    public bool NotFound { get; init; }
}

/// <summary>
/// Action-check response payload.
/// </summary>
public sealed class ConsoleActionCheckResponse
{
    /// <summary>Per-target evaluation results.</summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<ConsoleActionCheckResult> Results { get; init; }
}

/// <summary>
/// Provenance chain response payload.
/// </summary>
public sealed class ConsoleProvenanceChainResponse
{
    /// <summary>Anchor item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Items reachable from <see cref="ItemId"/> via provenance links, ordered
    /// by traversal depth. The anchor item itself is the first element.
    /// </summary>
    [JsonPropertyName("chain")]
    public required IReadOnlyList<ConsoleContentItem> Chain { get; init; }

    /// <summary>Maximum traversal depth applied.</summary>
    [JsonPropertyName("maxDepth")]
    public required int MaxDepth { get; init; }
}
