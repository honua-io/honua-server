// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

// Unknown, revoked, and expired tokens resolve as missing so the denial does not reveal which case it was.
public interface IConsoleShareStore
{
    Task<ConsoleShareState?> GetAsync(string itemId, CancellationToken cancellationToken = default);

    Task<ConsoleShareState> UpdateAccessTierAsync(string itemId, ConsoleShareAccessTier tier, string? principalId, CancellationToken cancellationToken = default);

    // The opaque value is on the mint result only, and the initial tier is kept only when no share state exists yet.
    Task<ConsolePublicLinkToken> MintPublicLinkAsync(string itemId, ConsoleShareAccessTier seedAccessTier, DateTimeOffset? expiresAt, string? principalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsolePublicLinkToken>> ListPublicLinksAsync(string itemId, CancellationToken cancellationToken = default);

    Task<bool> ExpirePublicLinkAsync(string itemId, string tokenId, string? principalId, CancellationToken cancellationToken = default);

    Task<ConsolePublicLinkResolution?> ResolvePublicLinkAsync(string token, CancellationToken cancellationToken = default);

    Task<ConsoleShareState> SetEmbedAsync(string itemId, ConsoleShareAccessTier seedAccessTier, bool enabled, ConsoleEmbedAudience? audience, string? principalId, CancellationToken cancellationToken = default);

    Task<ConsoleEmbedToken> MintEmbedTokenAsync(string itemId, ConsoleShareAccessTier seedAccessTier, ConsoleEmbedAudience audience, TimeSpan ttl, string? principalId, CancellationToken cancellationToken = default);

    Task<ConsoleEmbedTokenResolution?> RedeemEmbedTokenAsync(string token, CancellationToken cancellationToken = default);
}
