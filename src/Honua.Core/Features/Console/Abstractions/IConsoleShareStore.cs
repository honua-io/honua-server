// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

/// <summary>
/// Persistence contract for Console Share state — access tier, public-link
/// tokens, and embed tokens. Share state is authoritative for the share facet
/// of an item and is intentionally separate from <see cref="IConsoleContentStore"/>
/// (which owns membership visibility and item content).
/// </summary>
/// <remarks>
/// Token resolution (<see cref="ResolvePublicLinkAsync"/> /
/// <see cref="RedeemEmbedTokenAsync"/>) must return <see langword="null"/> for
/// unknown, revoked, or expired tokens so callers can apply a uniform,
/// non-leaking denial. Tier-coverage and presentation policy are applied by the
/// endpoint layer, not the store.
/// </remarks>
public interface IConsoleShareStore
{
    /// <summary>
    /// Returns the stored share state for an item, or <see langword="null"/> when
    /// no share state has been explicitly configured yet.
    /// </summary>
    Task<ConsoleShareState?> GetAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the authoritative access tier for an item, creating share state when
    /// none exists. Returns the updated state.
    /// </summary>
    Task<ConsoleShareState> UpdateAccessTierAsync(string itemId, ConsoleShareAccessTier tier, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a new public-link token. The returned token carries its opaque value
    /// (disclosed once); subsequent reads never re-emit it.
    /// </summary>
    Task<ConsolePublicLinkToken> MintPublicLinkAsync(string itemId, DateTimeOffset? expiresAt, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists public-link tokens for an item with token values omitted and
    /// effective expiry computed against the current clock.
    /// </summary>
    Task<IReadOnlyList<ConsolePublicLinkToken>> ListPublicLinksAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a public-link token by id (soft delete, retained for audit).
    /// Returns false when no matching token exists for the item.
    /// </summary>
    Task<bool> ExpirePublicLinkAsync(string itemId, string tokenId, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a public-link token value to its item and tier, or
    /// <see langword="null"/> when the token is unknown, revoked, or expired.
    /// </summary>
    Task<ConsolePublicLinkResolution?> ResolvePublicLinkAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables embedding for an item with the supplied audience,
    /// creating share state when none exists. Returns the updated state.
    /// </summary>
    Task<ConsoleShareState> SetEmbedAsync(string itemId, bool enabled, ConsoleEmbedAudience? audience, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints an embed token with the supplied audience and TTL. The returned token
    /// carries its opaque value (disclosed once).
    /// </summary>
    Task<ConsoleEmbedToken> MintEmbedTokenAsync(string itemId, ConsoleEmbedAudience audience, TimeSpan ttl, string? principalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems an embed token value, returning its resolution or
    /// <see langword="null"/> when the token is unknown or expired, or embedding
    /// is no longer enabled for the item.
    /// </summary>
    Task<ConsoleEmbedTokenResolution?> RedeemEmbedTokenAsync(string token, CancellationToken cancellationToken = default);
}
