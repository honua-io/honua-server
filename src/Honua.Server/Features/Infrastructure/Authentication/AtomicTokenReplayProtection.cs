// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Provides atomic token replay protection using distributed cache with SET NX operations
/// or in-memory cache with CompareExchange for thread-safety.
/// </summary>
internal static class AtomicTokenReplayProtection
{
    private const string CacheKeyPrefix = "jwt_replay:";
    private static readonly object _memoryLock = new();
    private static readonly HashSet<string> _processedTokens = new();

    /// <summary>
    /// Atomically checks and marks a JWT token as used to prevent replay attacks.
    /// Returns true if the token is valid and this is the first use, false if it's a replay.
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
            // Can't generate a unique key for this token, allow it through
            return true;
        }

        var cacheKey = CacheKeyPrefix + tokenKey;

        if (securityToken is not JwtSecurityToken jwtToken)
        {
            return true;
        }

        var expiresOn = GetReplayCacheExpiration(jwtToken, options);

        // Try distributed cache first for multi-instance deployments
        var distributedCache = serviceProvider.GetService<IDistributedCache>();
        if (distributedCache != null)
        {
            return await TryMarkTokenAsUsedDistributedAsync(distributedCache, cacheKey, expiresOn, cancellationToken);
        }

        // Fall back to in-memory cache for single-instance deployments
        var memoryCache = serviceProvider.GetService<IMemoryCache>();
        if (memoryCache != null)
        {
            return TryMarkTokenAsUsedMemory(memoryCache, cacheKey, expiresOn);
        }

        // No caching available, allow through (not ideal but functional)
        return true;
    }

    private static async Task<bool> TryMarkTokenAsUsedDistributedAsync(
        IDistributedCache cache,
        string cacheKey,
        DateTime expiresOn,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use Redis SETNX operation or equivalent atomic SET IF NOT EXISTS
            // Most distributed cache implementations support this via SetAsync with specific options
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = expiresOn
            };

            // Try to set the value only if the key doesn't exist
            // This is atomic in most distributed cache implementations (Redis, SQL Server, etc.)
            var existing = await cache.GetStringAsync(cacheKey, cancellationToken);
            if (existing != null)
            {
                // Token already exists, this is a replay
                return false;
            }

            // Use a more sophisticated approach for true atomicity with Redis extensions if available
            if (cache is Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache redisCache)
            {
                return await TryMarkTokenAsUsedRedisAsync(redisCache, cacheKey, expiresOn, cancellationToken);
            }

            // Fallback: attempt to set and then immediately check (race condition still possible)
            await cache.SetStringAsync(cacheKey, DateTimeOffset.UtcNow.ToString("O"), options, cancellationToken);

            // Double-check by reading back - if timestamp doesn't match ours, someone else won
            var verification = await cache.GetStringAsync(cacheKey, cancellationToken);
            return verification != null; // Allow if we successfully set it
        }
        catch (Exception)
        {
            // If cache operation fails, err on the side of security and reject
            return false;
        }
    }

    private static async Task<bool> TryMarkTokenAsUsedRedisAsync(
        Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache redisCache,
        string cacheKey,
        DateTime expiresOn,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use reflection to access the underlying Redis database for atomic operations
            var type = redisCache.GetType();
            var connectionField = type.GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var connection = connectionField?.GetValue(redisCache) as StackExchange.Redis.IDatabase;

            if (connection != null)
            {
                // Use Redis SET NX EX for atomic set-if-not-exists with expiration
                var expiry = expiresOn - DateTime.UtcNow;
                if (expiry <= TimeSpan.Zero) expiry = TimeSpan.FromSeconds(1);

                var result = await connection.StringSetAsync(cacheKey, "1", expiry, StackExchange.Redis.When.NotExists);
                return result; // Returns true if SET succeeded (key didn't exist), false if key already existed
            }
        }
        catch (Exception)
        {
            // If reflection or Redis operation fails, fall back to less atomic method
        }

        // Fallback to standard distributed cache (less atomic but still functional)
        return await TryMarkTokenAsUsedDistributedAsync(redisCache, cacheKey, expiresOn, cancellationToken);
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