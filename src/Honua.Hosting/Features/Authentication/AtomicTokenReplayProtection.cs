// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Provides atomic token replay protection using Redis SET NX for multi-replica deployments
/// or in-memory cache with a process-scoped lock for single-instance deployments.
///
/// BH5-022: Replaced the reflection-based IDatabase extraction from RedisCache (which
/// silently degraded to a non-atomic fallback on cold-start or after package renames) with
/// direct IConnectionMultiplexer resolution from the DI graph.
/// </summary>
internal static class AtomicTokenReplayProtection
{
    private const string CacheKeyPrefix = "jwt_replay:";
    private static readonly object _memoryLock = new();
    private static readonly HashSet<string> _processedTokens = new();

    /// <summary>
    /// Atomically checks and marks a JWT token as used to prevent replay attacks.
    /// Returns true if the token is valid and this is the first use, false if it is a replay.
    /// </summary>
    public static async Task<bool> TryMarkTokenAsUsedAsync(
        SecurityToken securityToken,
        TokenValidationOptions options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        var tokenKey = TryGetTokenReplayKey(securityToken);
        if (string.IsNullOrWhiteSpace(tokenKey))
        {
            return true;
        }

        var cacheKey = CacheKeyPrefix + tokenKey;

        if (securityToken is not JwtSecurityToken jwtToken)
        {
            return true;
        }

        var expiresOn = GetReplayCacheExpiration(jwtToken, options);

        // Resolve IConnectionMultiplexer directly - no reflection into IDistributedCache
        // internals. IConnectionMultiplexer is already in the DI graph when Redis is configured
        // (BH5-022: reflection fallback permanently degraded to non-atomic if the private field
        // is renamed by a package update).
        var multiplexer = serviceProvider.GetService<IConnectionMultiplexer>();
        if (multiplexer is not null)
        {
            try
            {
                var expiry = expiresOn - DateTime.UtcNow;
                if (expiry <= TimeSpan.Zero) expiry = TimeSpan.FromSeconds(1);
                // SET NX EX: atomic set-if-not-exists. Returns true when the key was set
                // (first use), false when the key already existed (replay).
                return await multiplexer.GetDatabase()
                    .StringSetAsync(cacheKey, "1", expiry, when: When.NotExists)
                    .ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // Redis unavailable: fail-closed - treat as replay to prevent replay via
                // a degraded non-atomic path.
                return false;
            }
        }

        // No Redis: fall back to in-memory cache with a process-scoped lock.
        var memoryCache = serviceProvider.GetService<IMemoryCache>();
        if (memoryCache != null)
        {
            return TryMarkTokenAsUsedMemory(memoryCache, cacheKey, expiresOn);
        }

        // No cache available; allow through (graceful degradation).
        return true;
    }

    private static bool TryMarkTokenAsUsedMemory(
        IMemoryCache cache,
        string cacheKey,
        DateTime expiresOn)
    {
        // Use thread-safe operations for in-memory cache
        lock (_memoryLock)
        {
            if (cache.TryGetValue(cacheKey, out _))
            {
                // Token already exists, this is a replay
                return false;
            }

            // Set the token as used
            cache.Set(cacheKey, true, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresOn,
                Size = 1 // Help with memory management
            });

            return true;
        }
    }

    private static string? TryGetTokenReplayKey(SecurityToken token)
    {
        if (token is JwtSecurityToken jwtToken)
        {
            // Prefer JTI claim if present (designed for replay prevention)
            if (!string.IsNullOrWhiteSpace(jwtToken.Id))
            {
                return $"jti:{jwtToken.Id}";
            }

            // Fall back to hash of the entire token
            if (!string.IsNullOrWhiteSpace(jwtToken.RawData))
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jwtToken.RawData));
                return $"hash:{Convert.ToHexStringLower(hash)}";
            }
        }

        return null;
    }

    private static DateTime GetReplayCacheExpiration(JwtSecurityToken token, TokenValidationOptions options)
    {
        var now = DateTime.UtcNow;
        var expires = token.ValidTo == DateTime.MinValue ? now : token.ValidTo.ToUniversalTime();

        // Apply configured cache duration limit
        if (options.TokenReplayCacheDuration > TimeSpan.Zero)
        {
            var maxExpiration = now.Add(options.TokenReplayCacheDuration);
            if (expires == DateTime.MinValue || expires > maxExpiration)
            {
                expires = maxExpiration;
            }
        }

        // Ensure expiration is in the future
        if (expires <= now)
        {
            expires = now.AddMinutes(5);
        }

        return expires;
    }
}
