// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// Request body for changing an item's Share access tier.
/// </summary>
public sealed class UpdateShareAccessRequest
{
    /// <summary>Target access tier.</summary>
    [Required]
    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    /// <summary>
    /// When true, a public-link/public-indexed change proceeds even if the
    /// dependency closure has conflicts. The bypass is recorded in telemetry.
    /// </summary>
    [JsonPropertyName("allowDependencyConflicts")]
    public bool AllowDependencyConflicts { get; init; }
}

/// <summary>
/// Request body for minting a public-link token.
/// </summary>
public sealed class MintPublicLinkRequest
{
    /// <summary>
    /// Optional expiry. Null mints a non-expiring token (valid until revoked).
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Request body for enabling or disabling embedding on an item.
/// </summary>
public sealed class SetEmbedRequest
{
    /// <summary>Whether embedding is enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Embed audience. Required when <see cref="Enabled"/> is true.</summary>
    [JsonPropertyName("audience")]
    public ConsoleEmbedAudience? Audience { get; init; }

    /// <summary>
    /// When true, enabling embed proceeds even if the dependency closure has
    /// conflicts. The bypass is recorded in telemetry.
    /// </summary>
    [JsonPropertyName("allowDependencyConflicts")]
    public bool AllowDependencyConflicts { get; init; }
}

/// <summary>
/// Request body for minting an embed token.
/// </summary>
public sealed class MintEmbedTokenRequest
{
    /// <summary>Embed audience the token authorizes.</summary>
    [Required]
    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    /// <summary>
    /// Token lifetime in seconds. Defaults to 3600 (one hour) when omitted;
    /// bounded to 24 hours.
    /// </summary>
    [Range(1, 86_400)]
    [JsonPropertyName("ttlSeconds")]
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// Dependency-closure evaluation response. Returned on the dependency preview
/// endpoint and as the body of a 409 when a tier change is blocked.
/// </summary>
public sealed class ConsoleShareDependencyClosureResponse
{
    /// <summary>Root item the closure was evaluated for.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Target tier the closure was evaluated against.</summary>
    [JsonPropertyName("targetTier")]
    public required ConsoleShareAccessTier TargetTier { get; init; }

    /// <summary>True when the closure is compatible (no conflicts).</summary>
    [JsonPropertyName("isCompatible")]
    public required bool IsCompatible { get; init; }

    /// <summary>Dependencies blocking the target tier.</summary>
    [JsonPropertyName("conflicts")]
    public required IReadOnlyList<ConsoleShareDependencyConflict> Conflicts { get; init; }
}

/// <summary>
/// Public-link token list response. Token values are always null here.
/// </summary>
public sealed class ConsolePublicLinkListResponse
{
    /// <summary>Item the tokens belong to.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Tokens with values omitted and effective expiry applied.</summary>
    [JsonPropertyName("tokens")]
    public required IReadOnlyList<ConsolePublicLinkToken> Tokens { get; init; }
}

/// <summary>
/// Anonymous-safe public-link resolution response. Carries only presentation
/// metadata intended for a shared item — never owner, provenance, caller
/// permissions, or other private fields.
/// </summary>
public sealed class ConsolePublicLinkResolutionResponse
{
    /// <summary>Resolved item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Item category.</summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Display title, when set.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Short presentation summary, when set.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>Effective access tier of the shared item.</summary>
    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    /// <summary>Logical access endpoints keyed by relation.</summary>
    [JsonPropertyName("endpoints")]
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }
}

/// <summary>
/// Anonymous-safe embed redemption response.
/// </summary>
public sealed class ConsoleEmbedRedeemResponse
{
    /// <summary>Embedded item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Item category.</summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Display title, when set.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Audience the embed is authorized to render.</summary>
    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    /// <summary>Hard expiry of the redeemed token.</summary>
    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Logical access endpoints keyed by relation.</summary>
    [JsonPropertyName("endpoints")]
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }
}
