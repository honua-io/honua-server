// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Server-owned Share access projection for a Console content item. This is the
/// read model Console renders the Share panel from without resolving share state
/// client-side. It is a computed view (never stored) that joins the content item
/// identity with the item's share state and the requesting principal's
/// permissions.
/// </summary>
public sealed record ConsoleShareProjection
{
    /// <summary>Content item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Machine-friendly item name.</summary>
    [JsonPropertyName("itemName")]
    public required string ItemName { get; init; }

    /// <summary>Human-readable item title, when set.</summary>
    [JsonPropertyName("itemTitle")]
    public string? ItemTitle { get; init; }

    /// <summary>Item category.</summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Owner principal id.</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>
    /// Effective Share access tier. Authoritative share state when explicitly
    /// configured; otherwise derived from the item's membership visibility.
    /// </summary>
    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    /// <summary>
    /// True when at least one non-expired public-link token exists for the item.
    /// </summary>
    [JsonPropertyName("publicLinkEnabled")]
    public bool PublicLinkEnabled { get; init; }

    /// <summary>
    /// Active and historical public-link tokens (token value always null here).
    /// </summary>
    [JsonPropertyName("publicLinkTokens")]
    public IReadOnlyList<ConsolePublicLinkToken> PublicLinkTokens { get; init; } = Array.Empty<ConsolePublicLinkToken>();

    /// <summary>True when embedding is enabled for the item.</summary>
    [JsonPropertyName("embedEnabled")]
    public bool EmbedEnabled { get; init; }

    /// <summary>Configured embed audience when <see cref="EmbedEnabled"/> is true.</summary>
    [JsonPropertyName("embedAudience")]
    public ConsoleEmbedAudience? EmbedAudience { get; init; }

    /// <summary>
    /// Summary flag: whether the item is currently eligible for open-data
    /// distribution (a distributable item type that is public-indexed). The full
    /// open-data publication workflow is tracked separately.
    /// </summary>
    [JsonPropertyName("openDataEligible")]
    public bool OpenDataEligible { get; init; }

    /// <summary>
    /// Console verbs the requesting principal may perform on the item — includes
    /// the <c>share</c>/<c>embed</c>/<c>administer</c> facets the Share panel
    /// gates on. Server-authored.
    /// </summary>
    [JsonPropertyName("callerPermissions")]
    public IReadOnlyList<ConsoleContentAction> CallerPermissions { get; init; } = Array.Empty<ConsoleContentAction>();

    /// <summary>
    /// True when an anonymous caller could read the item through any current
    /// share mechanism (public-indexed, an active public link, or embed).
    /// </summary>
    [JsonPropertyName("anonymousEligible")]
    public bool AnonymousEligible { get; init; }

    /// <summary>
    /// Logical access endpoints for the item keyed by relation (e.g. <c>self</c>).
    /// </summary>
    [JsonPropertyName("endpoints")]
    public IReadOnlyDictionary<string, string> Endpoints { get; init; } = new Dictionary<string, string>();

    /// <summary>Timestamp the share state was last updated, when state exists.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Principal that last updated the share state, when state exists.</summary>
    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

/// <summary>
/// A single dependency that blocks a public sharing or embed enablement request
/// because it is not shareable by the requested target audience.
/// </summary>
public sealed record ConsoleShareDependencyConflict
{
    /// <summary>Identifier of the blocking provenance item.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Item category of the blocking item, when resolvable.</summary>
    [JsonPropertyName("itemType")]
    public ConsoleContentItemType? ItemType { get; init; }

    /// <summary>
    /// Display name of the blocking item. Populated for authenticated callers;
    /// omitted on anonymous surfaces so private titles are never leaked.
    /// </summary>
    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    /// <summary>
    /// Stable, human-readable reason the dependency is incompatible (e.g.
    /// <c>"item visibility is 'personal'"</c>).
    /// </summary>
    [JsonPropertyName("blockingReason")]
    public required string BlockingReason { get; init; }
}
