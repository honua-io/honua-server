// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Authoritative, persisted Share state for a content item. This is an internal
/// store record — it is never serialized to the wire directly; endpoints project
/// it into <see cref="ConsoleShareProjection"/> (joined with the content item)
/// or into a resolution record for anonymous reads.
/// </summary>
/// <remarks>
/// Token records carry their opaque value (<see cref="ConsolePublicLinkToken.Token"/>
/// / <see cref="ConsoleEmbedToken.Token"/>) populated for resolution. A token's
/// stored <c>IsExpired</c> flag is the explicit revocation bit; the store ORs it
/// with a clock check when returning tokens, so callers always observe effective
/// expiry. The persistent (Postgres) implementation must store a SHA-256 hash of
/// the token value rather than plaintext.
/// </remarks>
public sealed record ConsoleShareState
{
    /// <summary>Content item id this state belongs to.</summary>
    public required string ItemId { get; init; }

    /// <summary>Authoritative Share access tier.</summary>
    public ConsoleShareAccessTier AccessTier { get; init; } = ConsoleShareAccessTier.Private;

    /// <summary>Whether embedding is enabled.</summary>
    public bool EmbedEnabled { get; init; }

    /// <summary>Configured embed audience when <see cref="EmbedEnabled"/> is true.</summary>
    public ConsoleEmbedAudience? EmbedAudience { get; init; }

    /// <summary>Public-link tokens minted for the item (active and revoked).</summary>
    public IReadOnlyList<ConsolePublicLinkToken> PublicLinkTokens { get; init; } = Array.Empty<ConsolePublicLinkToken>();

    /// <summary>Embed tokens minted for the item.</summary>
    public IReadOnlyList<ConsoleEmbedToken> EmbedTokens { get; init; } = Array.Empty<ConsoleEmbedToken>();

    /// <summary>Timestamp the state was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Principal that last updated the state (audit).</summary>
    public string? UpdatedById { get; init; }
}

/// <summary>
/// Result of resolving a public-link token. Carries only the minimum the public
/// endpoint needs to build an anonymous-safe response; presentation metadata
/// (title/summary) is read separately from the content store at resolve time.
/// </summary>
public sealed record ConsolePublicLinkResolution
{
    /// <summary>Content item the token resolves to.</summary>
    public required string ItemId { get; init; }

    /// <summary>Effective Share access tier of the item at resolve time.</summary>
    public required ConsoleShareAccessTier AccessTier { get; init; }

    /// <summary>Stable id of the resolved token (for telemetry attribution).</summary>
    public required string TokenId { get; init; }
}

/// <summary>
/// Result of redeeming an embed token. Carries the item id, audience, and TTL
/// the public endpoint needs to build an anonymous-safe embed resolution.
/// </summary>
public sealed record ConsoleEmbedTokenResolution
{
    /// <summary>Content item the token authorizes embedding for.</summary>
    public required string ItemId { get; init; }

    /// <summary>Audience the embed is authorized to render.</summary>
    public required ConsoleEmbedAudience Audience { get; init; }

    /// <summary>Hard expiry of the redeemed token.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Stable id of the redeemed token (for telemetry attribution).</summary>
    public required string TokenId { get; init; }
}
