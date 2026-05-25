// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Baseline in-memory <see cref="IConsoleShareStore"/>. Suitable for tests and
/// early Console wiring; a persistent implementation lands in a follow-on ticket
/// alongside the persistent content store (#1163).
/// </summary>
/// <remarks>
/// Token values are held in plaintext here, which is acceptable for an in-memory
/// MVP store. The persistent implementation MUST store a SHA-256 hash of the
/// token value and compare on lookup so tokens cannot be read back out of the
/// database. Resolution does a linear scan over token records — acceptable for
/// in-memory, but the persistent store should index the token (hash) column.
/// </remarks>
internal sealed class InMemoryConsoleShareStore : IConsoleShareStore
{
    private const int TokenByteLength = 32;

    private readonly ConcurrentDictionary<string, ConsoleShareState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryConsoleShareStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<ConsoleShareState?> GetAsync(string itemId, CancellationToken cancellationToken = default)
    {
        _states.TryGetValue(itemId, out var state);
        return Task.FromResult(state);
    }

    public Task<ConsoleShareState> UpdateAccessTierAsync(string itemId, ConsoleShareAccessTier tier, string? principalId, CancellationToken cancellationToken = default)
    {
        var updated = Mutate(itemId, principalId, state => state with { AccessTier = tier });
        return Task.FromResult(updated);
    }

    public Task<ConsolePublicLinkToken> MintPublicLinkAsync(string itemId, DateTimeOffset? expiresAt, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var token = new ConsolePublicLinkToken
        {
            TokenId = NewTokenId(),
            Token = NewTokenValue(),
            ItemId = itemId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedById = principalId,
            IsExpired = false,
        };

        Mutate(itemId, principalId, state => state with
        {
            PublicLinkTokens = Append(state.PublicLinkTokens, token),
        });

        // Return the minted token verbatim — the opaque value is disclosed once.
        return Task.FromResult(token);
    }

    public Task<IReadOnlyList<ConsolePublicLinkToken>> ListPublicLinksAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(itemId, out var state))
        {
            return Task.FromResult<IReadOnlyList<ConsolePublicLinkToken>>(Array.Empty<ConsolePublicLinkToken>());
        }

        var now = _timeProvider.GetUtcNow();
        IReadOnlyList<ConsolePublicLinkToken> projected = state.PublicLinkTokens
            .Select(t => Redact(WithEffectiveExpiry(t, now)))
            .ToList();
        return Task.FromResult(projected);
    }

    public Task<bool> ExpirePublicLinkAsync(string itemId, string tokenId, string? principalId, CancellationToken cancellationToken = default)
    {
        // CAS loop so a concurrent mint/expire cannot clobber the soft-delete.
        while (true)
        {
            if (!_states.TryGetValue(itemId, out var existing))
            {
                return Task.FromResult(false);
            }

            var index = IndexOfToken(existing.PublicLinkTokens, tokenId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var tokens = existing.PublicLinkTokens.ToArray();
            if (tokens[index].IsExpired)
            {
                // Already revoked; idempotent success.
                return Task.FromResult(true);
            }
            tokens[index] = tokens[index] with { IsExpired = true };

            var updated = existing with
            {
                PublicLinkTokens = tokens,
                UpdatedAt = _timeProvider.GetUtcNow(),
                UpdatedById = principalId ?? existing.UpdatedById,
            };
            if (_states.TryUpdate(itemId, updated, existing))
            {
                return Task.FromResult(true);
            }
        }
    }

    public Task<ConsolePublicLinkResolution?> ResolvePublicLinkAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult<ConsolePublicLinkResolution?>(null);
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var state in _states.Values)
        {
            foreach (var candidate in state.PublicLinkTokens)
            {
                if (!string.Equals(candidate.Token, token, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsTokenExpired(candidate, now))
                {
                    // Found but no longer valid — deny without disclosing the item.
                    return Task.FromResult<ConsolePublicLinkResolution?>(null);
                }

                return Task.FromResult<ConsolePublicLinkResolution?>(new ConsolePublicLinkResolution
                {
                    ItemId = state.ItemId,
                    AccessTier = state.AccessTier,
                    TokenId = candidate.TokenId,
                });
            }
        }

        return Task.FromResult<ConsolePublicLinkResolution?>(null);
    }

    public Task<ConsoleShareState> SetEmbedAsync(string itemId, bool enabled, ConsoleEmbedAudience? audience, string? principalId, CancellationToken cancellationToken = default)
    {
        var updated = Mutate(itemId, principalId, state => state with
        {
            EmbedEnabled = enabled,
            EmbedAudience = enabled ? audience : null,
        });
        return Task.FromResult(updated);
    }

    public Task<ConsoleEmbedToken> MintEmbedTokenAsync(string itemId, ConsoleEmbedAudience audience, TimeSpan ttl, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var token = new ConsoleEmbedToken
        {
            TokenId = NewTokenId(),
            Token = NewTokenValue(),
            ItemId = itemId,
            Audience = audience,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
            CreatedById = principalId,
            IsExpired = false,
        };

        Mutate(itemId, principalId, state => state with
        {
            EmbedTokens = Append(state.EmbedTokens, token),
        });

        return Task.FromResult(token);
    }

    public Task<ConsoleEmbedTokenResolution?> RedeemEmbedTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult<ConsoleEmbedTokenResolution?>(null);
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var state in _states.Values)
        {
            foreach (var candidate in state.EmbedTokens)
            {
                if (!string.Equals(candidate.Token, token, StringComparison.Ordinal))
                {
                    continue;
                }

                // Embedding being switched off revokes outstanding tokens, and a
                // token past its hard TTL no longer redeems. Both deny uniformly.
                if (!state.EmbedEnabled || candidate.IsExpired || candidate.ExpiresAt <= now)
                {
                    return Task.FromResult<ConsoleEmbedTokenResolution?>(null);
                }

                return Task.FromResult<ConsoleEmbedTokenResolution?>(new ConsoleEmbedTokenResolution
                {
                    ItemId = state.ItemId,
                    Audience = candidate.Audience,
                    ExpiresAt = candidate.ExpiresAt,
                    TokenId = candidate.TokenId,
                });
            }
        }

        return Task.FromResult<ConsoleEmbedTokenResolution?>(null);
    }

    /// <summary>
    /// Applies <paramref name="mutator"/> to the item's state under a
    /// compare-and-swap loop, seeding default state when none exists. Always
    /// refreshes the audit stamp.
    /// </summary>
    private ConsoleShareState Mutate(string itemId, string? principalId, Func<ConsoleShareState, ConsoleShareState> mutator)
    {
        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            if (_states.TryGetValue(itemId, out var existing))
            {
                var updated = mutator(existing) with
                {
                    ItemId = itemId,
                    UpdatedAt = now,
                    UpdatedById = principalId ?? existing.UpdatedById,
                };
                if (_states.TryUpdate(itemId, updated, existing))
                {
                    return updated;
                }

                continue;
            }

            var seeded = mutator(new ConsoleShareState { ItemId = itemId }) with
            {
                ItemId = itemId,
                UpdatedAt = now,
                UpdatedById = principalId,
            };
            if (_states.TryAdd(itemId, seeded))
            {
                return seeded;
            }
        }
    }

    private static List<ConsolePublicLinkToken> Append(IReadOnlyList<ConsolePublicLinkToken> tokens, ConsolePublicLinkToken token)
    {
        var next = new List<ConsolePublicLinkToken>(tokens.Count + 1);
        next.AddRange(tokens);
        next.Add(token);
        return next;
    }

    private static List<ConsoleEmbedToken> Append(IReadOnlyList<ConsoleEmbedToken> tokens, ConsoleEmbedToken token)
    {
        var next = new List<ConsoleEmbedToken>(tokens.Count + 1);
        next.AddRange(tokens);
        next.Add(token);
        return next;
    }

    private static int IndexOfToken(IReadOnlyList<ConsolePublicLinkToken> tokens, string tokenId)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i].TokenId, tokenId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTokenExpired(ConsolePublicLinkToken token, DateTimeOffset now)
        => token.IsExpired || (token.ExpiresAt is { } expiry && expiry <= now);

    private static ConsolePublicLinkToken WithEffectiveExpiry(ConsolePublicLinkToken token, DateTimeOffset now)
        => token with { IsExpired = IsTokenExpired(token, now) };

    private static ConsolePublicLinkToken Redact(ConsolePublicLinkToken token)
        => token with { Token = null };

    private static string NewTokenId() => Guid.NewGuid().ToString("N");

    private static string NewTokenValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
}
