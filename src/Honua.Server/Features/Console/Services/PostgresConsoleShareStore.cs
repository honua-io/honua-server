// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Configuration;

namespace Honua.Server.Features.Console.Services;

// Share state in the system database: access tier, public links and the embed switch are one document per item
// under the shared configuration generation (ADR-0086), so a revoked link stops resolving on every task. Embed
// tokens are short-lived and minted per viewer, so they live with the other expiring tokens and change no generation.
internal sealed class PostgresConsoleShareStore(
    PostgresConfigurationDocuments documents,
    ISecurityTokenStore embedTokens,
    TimeProvider timeProvider,
    IHttpContextAccessor? contexts = null) : IConsoleShareStore
{
    private const int TokenByteLength = 32;

    private readonly ConfigurationCollectionStore<StoredConsoleShare> _shares = new(
        documents, new("console-share", ConsoleStateJsonContext.Default.StoredConsoleShare), contexts);
    private LinkIndex? _links;

    public async Task<ConsoleShareState?> GetAsync(string itemId, CancellationToken cancellationToken = default)
        => (await _shares.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(itemId) is { } stored
            ? ToState(stored)
            : null;

    public async Task<ConsoleShareState> UpdateAccessTierAsync(
        string itemId, ConsoleShareAccessTier tier, string? principalId, CancellationToken cancellationToken = default)
        => ToState(await ChangeAsync(itemId, tier, principalId, share => share with { AccessTier = tier }, cancellationToken)
            .ConfigureAwait(false));

    public async Task<ConsolePublicLinkToken> MintPublicLinkAsync(
        string itemId, ConsoleShareAccessTier seedAccessTier, DateTimeOffset? expiresAt, string? principalId,
        CancellationToken cancellationToken = default)
    {
        var token = new ConsolePublicLinkToken
        {
            TokenId = NewTokenId(),
            Token = NewTokenValue(),
            ItemId = itemId,
            CreatedAt = timeProvider.GetUtcNow(),
            ExpiresAt = expiresAt,
            CreatedById = principalId,
            IsExpired = false,
        };
        var link = new StoredConsolePublicLink(token.TokenId, Hash(token.Token), token.CreatedAt, expiresAt, principalId, IsExpired: false);
        await ChangeAsync(itemId, seedAccessTier, principalId,
            share => share with { PublicLinks = [.. share.PublicLinks, link] }, cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<IReadOnlyList<ConsolePublicLinkToken>> ListPublicLinksAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var shares = await _shares.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        if (!shares.TryGetValue(itemId, out var share))
        {
            return [];
        }
        var now = timeProvider.GetUtcNow();
        return share.PublicLinks.Select(link => ToToken(itemId, link) with { IsExpired = IsExpired(link, now) }).ToArray();
    }

    public async Task<bool> ExpirePublicLinkAsync(string itemId, string tokenId, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var found = false;
        await _shares.MutateAsync(itemId, existing =>
        {
            var link = existing?.PublicLinks.FirstOrDefault(candidate => string.Equals(candidate.TokenId, tokenId, StringComparison.Ordinal));
            found = link is not null;
            if (existing is null || link is null || link.IsExpired)
            {
                return existing;
            }
            return existing with
            {
                PublicLinks = existing.PublicLinks.Select(candidate => ReferenceEquals(candidate, link) ? link with { IsExpired = true } : candidate).ToArray(),
                UpdatedAt = now,
                UpdatedById = principalId ?? existing.UpdatedById,
            };
        }, cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<ConsolePublicLinkResolution?> ResolvePublicLinkAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        var links = await ReadLinksAsync(cancellationToken).ConfigureAwait(false);
        if (!links.TryGetValue(Hash(token), out var entry) || IsExpired(entry.Link, timeProvider.GetUtcNow()))
        {
            return null;
        }
        return new ConsolePublicLinkResolution
        {
            ItemId = entry.Share.ItemId,
            AccessTier = entry.Share.AccessTier,
            TokenId = entry.Link.TokenId,
        };
    }

    public async Task<ConsoleShareState> SetEmbedAsync(
        string itemId, ConsoleShareAccessTier seedAccessTier, bool enabled, ConsoleEmbedAudience? audience, string? principalId,
        CancellationToken cancellationToken = default)
        => ToState(await ChangeAsync(itemId, seedAccessTier, principalId,
            share => share with { EmbedEnabled = enabled, EmbedAudience = enabled ? audience : null }, cancellationToken)
            .ConfigureAwait(false));

    public async Task<ConsoleEmbedToken> MintEmbedTokenAsync(
        string itemId, ConsoleShareAccessTier seedAccessTier, ConsoleEmbedAudience audience, TimeSpan ttl, string? principalId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
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
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new StoredConsoleEmbedToken(token.TokenId, itemId, audience, token.ExpiresAt),
            ConsoleStateJsonContext.Default.StoredConsoleEmbedToken);
        await embedTokens.StoreAsync(new(SecurityTokenKind.ConsoleEmbedToken, token.Token), payload, ttl, cancellationToken)
            .ConfigureAwait(false);
        return token;
    }

    // Switching embedding off refuses every outstanding token, and switching it on again accepts those not yet expired.
    public async Task<ConsoleEmbedTokenResolution?> RedeemEmbedTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        var stored = await embedTokens.FindAsync(new(SecurityTokenKind.ConsoleEmbedToken, token), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }
        var minted = JsonSerializer.Deserialize(stored.Payload, ConsoleStateJsonContext.Default.StoredConsoleEmbedToken)
            ?? throw new JsonException("Embed token payload is empty.");
        var shares = await _shares.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        if (!shares.TryGetValue(minted.ItemId, out var share) || !share.EmbedEnabled || minted.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return null;
        }
        return new ConsoleEmbedTokenResolution
        {
            ItemId = minted.ItemId,
            Audience = minted.Audience,
            ExpiresAt = minted.ExpiresAt,
            TokenId = minted.TokenId,
        };
    }

    // The first write for an item keeps the caller's effective tier instead of resetting it to private.
    private async Task<StoredConsoleShare> ChangeAsync(
        string itemId, ConsoleShareAccessTier seedAccessTier, string? principalId,
        Func<StoredConsoleShare, StoredConsoleShare> change, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var updated = await _shares.MutateAsync(itemId, existing =>
            change(existing ?? new StoredConsoleShare { ItemId = itemId, AccessTier = seedAccessTier }) with
            {
                ItemId = itemId,
                UpdatedAt = now,
                UpdatedById = principalId ?? existing?.UpdatedById,
            }, cancellationToken).ConfigureAwait(false);
        return updated ?? throw new InvalidOperationException("Share state was not stored.");
    }

    private async Task<IReadOnlyDictionary<string, (StoredConsoleShare Share, StoredConsolePublicLink Link)>> ReadLinksAsync(
        CancellationToken cancellationToken)
    {
        var shares = await _shares.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        var links = Volatile.Read(ref _links);
        if (links is null || !ReferenceEquals(links.Shares, shares))
        {
            links = new(shares, shares.Values
                .SelectMany(share => share.PublicLinks.Select(link => (Share: share, Link: link)))
                .ToDictionary(static entry => entry.Link.TokenHash, StringComparer.Ordinal));
            Volatile.Write(ref _links, links);
        }
        return links.ByTokenHash;
    }

    private static ConsoleShareState ToState(StoredConsoleShare share) => new()
    {
        ItemId = share.ItemId,
        AccessTier = share.AccessTier,
        EmbedEnabled = share.EmbedEnabled,
        EmbedAudience = share.EmbedAudience,
        PublicLinkTokens = share.PublicLinks.Select(link => ToToken(share.ItemId, link)).ToArray(),
        UpdatedAt = share.UpdatedAt,
        UpdatedById = share.UpdatedById,
    };

    private static ConsolePublicLinkToken ToToken(string itemId, StoredConsolePublicLink link) => new()
    {
        TokenId = link.TokenId,
        ItemId = itemId,
        CreatedAt = link.CreatedAt,
        ExpiresAt = link.ExpiresAt,
        CreatedById = link.CreatedById,
        IsExpired = link.IsExpired,
    };

    private static bool IsExpired(StoredConsolePublicLink link, DateTimeOffset now)
        => link.IsExpired || (link.ExpiresAt is { } expiry && expiry <= now);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewTokenId() => Guid.NewGuid().ToString("N");

    private static string NewTokenValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

    private sealed record LinkIndex(
        IReadOnlyDictionary<string, StoredConsoleShare> Shares,
        Dictionary<string, (StoredConsoleShare Share, StoredConsolePublicLink Link)> ByTokenHash);
}
