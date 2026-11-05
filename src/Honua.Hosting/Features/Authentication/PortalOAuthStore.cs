// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class PortalOAuthStore(
    IMemoryCache memoryCache,
    ILogger<PortalOAuthStore> logger,
    IDistributedCache? distributedCache = null)
{
    private const string BrokerKeyPrefix = "portal-oauth:broker:";
    private const string CodeKeyPrefix = "portal-oauth:code:";
    private const string RefreshKeyPrefix = "portal-oauth:refresh:";
    private const string RevokeKeyPrefix = "portal-oauth:revoke:";
    // Prefix for the short-lived Redis claim keys used by AtomicConsumeAsync to ensure
    // single-use semantics under concurrent requests (BH2-022, BH2-023).
    private const string ConsumeClaimPrefix = "portal-oauth:claim:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<PortalOAuthStore> _logger = logger;
    private readonly IDistributedCache? _distributedCache = distributedCache;
    // Lock used to make TryGetValue+Remove atomic on the memory-only path (BH2-022).
    private readonly Lock _consumeLock = new();

    public async Task<string> CreateBrokerSessionAsync(
        PortalOAuthBrokerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = CreateValue();
        await SetAsync(
            BrokerKeyPrefix + id,
            session,
            session.ExpiresAt,
            PortalOAuthStoreJsonContext.Default.PortalOAuthBrokerSession,
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public Task<PortalOAuthBrokerSession?> ConsumeBrokerSessionAsync(
        string? id,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            BrokerKeyPrefix,
            id,
            PortalOAuthStoreJsonContext.Default.PortalOAuthBrokerSession,
            cancellationToken);

    public async Task<string> CreateAuthorizationCodeAsync(
        PortalOAuthAuthorizationCode code,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        var value = CreateValue();
        await SetAsync(
            CodeKeyPrefix + value,
            code,
            code.ExpiresAt,
            PortalOAuthStoreJsonContext.Default.PortalOAuthAuthorizationCode,
            cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<PortalOAuthAuthorizationCode?> ConsumeAuthorizationCodeAsync(
        string? code,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            CodeKeyPrefix,
            code,
            PortalOAuthStoreJsonContext.Default.PortalOAuthAuthorizationCode,
            cancellationToken);

    public Task RevokePrincipalAsync(string principalKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalKey))
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var marker = new PortalOAuthRefreshRevocation
        {
            RevokedAt = now,
            ExpiresAt = now.AddDays(91),
        };
        return SetAsync(
            RevokeKeyPrefix + principalKey.Trim(),
            marker,
            marker.ExpiresAt,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshRevocation,
            cancellationToken);
    }

    public async Task<bool> IsPrincipalRefreshRevokedAsync(
        string? principalKey,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalKey))
        {
            return false;
        }

        var marker = await GetAsync(
            RevokeKeyPrefix,
            principalKey.Trim(),
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshRevocation,
            cancellationToken).ConfigureAwait(false);
        return marker is not null && issuedAt <= marker.RevokedAt;
    }

    public async Task<string> CreateRefreshTokenAsync(
        PortalOAuthRefreshToken token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var value = CreateValue();
        await SetAsync(
            RefreshKeyPrefix + value,
            token,
            token.ExpiresAt,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<PortalOAuthRefreshToken?> GetRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => GetAsync(
            RefreshKeyPrefix,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    public Task RemoveRefreshTokenAsync(string? token, CancellationToken cancellationToken)
        => RemoveAsync(RefreshKeyPrefix, token, cancellationToken);

    public Task<PortalOAuthRefreshToken?> ConsumeRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            RefreshKeyPrefix,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    // A refresh token consumed before issuance failed is restored under its original value with the remaining lifetime so the client can retry.
    public Task RestoreRefreshTokenAsync(
        string tokenValue,
        PortalOAuthRefreshToken record,
        CancellationToken cancellationToken)
        => SetAsync(
            RefreshKeyPrefix + tokenValue,
            record,
            record.ExpiresAt,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    private async Task SetAsync<T>(
        string key,
        T value,
        DateTimeOffset expiresAt,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (_distributedCache is null)
        {
            _memoryCache.Set(key, payload, expiresAt);
            return;
        }

        try
        {
            await _distributedCache.SetAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt },
                cancellationToken).ConfigureAwait(false);
            _memoryCache.Set(key, payload, expiresAt);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(key);
            // Surface persistence failures so a single-node-only artifact is never
            // mistaken for a durable one (same rationale as AdminAuthSessionStore).
            PortalOAuthLog.StorePersistFailed(_logger, LogValueRedactor.Hash(key), ex);
            throw;
        }
    }

    private async Task<T?> GetAsync<T>(
        string keyPrefix,
        string? id,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : PortalOAuthRecord
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var key = keyPrefix + id;
        byte[]? payload = null;

        if (_distributedCache is not null)
        {
            try
            {
                payload = await _distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    _memoryCache.Remove(key);
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional: any distributed-cache failure degrades to a cache miss
                // (already logged) rather than surfacing to the OAuth request path.
                _memoryCache.Remove(key);
                PortalOAuthLog.StoreReadFailed(_logger, LogValueRedactor.Hash(key), ex);
                return null;
            }
        }
        else if (!_memoryCache.TryGetValue(key, out payload) || payload is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(payload, typeInfo);
        if (value is null || value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RemoveAsync(keyPrefix, id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        _memoryCache.Set(key, payload, value.ExpiresAt);
        return value;
    }

    private async Task RemoveAsync(string keyPrefix, string? id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var key = keyPrefix + id;
        _memoryCache.Remove(key);

        if (_distributedCache is null)
        {
            return;
        }

        try
        {
            await _distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional: removal is best-effort; the entry's own TTL is the safety net,
            // and the failure is already logged for diagnosis.
            PortalOAuthLog.StoreRemoveFailed(_logger, LogValueRedactor.Hash(key), ex);
        }
    }

    // An authorization code or rotated refresh token is taken atomically; a separate read and delete would let two callers mint from it.
    private async Task<T?> AtomicConsumeAsync<T>(
        string keyPrefix,
        string? id,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : PortalOAuthRecord
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var key = keyPrefix + id;

        if (_distributedCache is null)
        {
            // Memory-only path: lock makes TryGetValue+Remove atomic within this process.
            byte[]? memPayload;
            lock (_consumeLock)
            {
                if (!_memoryCache.TryGetValue(key, out memPayload) || memPayload is null)
                {
                    return null;
                }
                _memoryCache.Remove(key);
            }
            var memValue = JsonSerializer.Deserialize(memPayload, typeInfo);
            return memValue is null || memValue.ExpiresAt <= DateTimeOffset.UtcNow ? null : memValue;
        }

        // Distributed path: acquire a short-lived claim lock via Redis SET NX so only
        // one concurrent caller can proceed with GET+DEL. The claim key uses a separate
        // Redis STRING key that does not interfere with the distributed cache's hash format.
        var claimKey = (StackExchange.Redis.RedisKey)(ConsumeClaimPrefix + key);
        var redisDb = TryGetRedisDatabase();
        var claimAcquired = false;

        if (redisDb is not null)
        {
            try
            {
                claimAcquired = await redisDb.StringSetAsync(
                    claimKey,
                    "1",
                    TimeSpan.FromSeconds(30),
                    StackExchange.Redis.When.NotExists).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Redis claim failed (connection issue); fall through to non-atomic GET+DEL.
                PortalOAuthLog.ClaimLockAcquireFailed(_logger, LogValueRedactor.Hash(key), ex);
                redisDb = null;
            }

            if (redisDb is not null && !claimAcquired)
            {
                // Claim not acquired: another concurrent caller holds it.
                _memoryCache.Remove(key);
                return null;
            }
        }

        try
        {
            byte[]? payload;
            try
            {
                payload = await _distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional: any distributed-cache failure degrades to a cache miss
                // (already logged) rather than surfacing to the OAuth request path.
                _memoryCache.Remove(key);
                PortalOAuthLog.StoreReadFailed(_logger, LogValueRedactor.Hash(key), ex);
                return null;
            }

            if (payload is null)
            {
                _memoryCache.Remove(key);
                return null;
            }

            // Remove from both distributed cache and L1 memory cache.
            try
            {
                await _distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional: removal is best-effort; the entry's own TTL is the safety
                // net, and the failure is already logged for diagnosis.
                PortalOAuthLog.StoreRemoveFailed(_logger, LogValueRedactor.Hash(key), ex);
            }
            _memoryCache.Remove(key);

            var value = JsonSerializer.Deserialize(payload, typeInfo);
            return value is null || value.ExpiresAt <= DateTimeOffset.UtcNow ? null : value;
        }
        finally
        {
            // Release the claim lock. The 30 s TTL is the safety net if this fails.
            if (redisDb is not null && claimAcquired)
            {
                try
                {
                    await redisDb.KeyDeleteAsync(claimKey).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Ignored; the claim key expires automatically via its TTL.
                    PortalOAuthLog.ClaimLockReleaseFailed(_logger, LogValueRedactor.Hash(key), ex);
                }
            }
        }
    }

    private StackExchange.Redis.IDatabase? TryGetRedisDatabase()
    {
        if (_distributedCache is not Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache redisCache)
        {
            return null;
        }

        try
        {
            var field = typeof(Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache)
                .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(redisCache) as StackExchange.Redis.IDatabase;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional: reflection access is best-effort and commonly unavailable on
            // first-request cold start; callers fall back to non-atomic cache operations.
            PortalOAuthLog.RedisDatabaseUnavailable(_logger, ex);
            return null;
        }
    }

    private static string CreateValue()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

internal abstract class PortalOAuthRecord
{
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class PortalOAuthBrokerSession : PortalOAuthRecord
{
    public required string ProviderKey { get; init; }

    public required string ClientId { get; init; }

    public required string RedirectUri { get; init; }

    public string? ClientState { get; init; }

    public string? ClientCodeChallenge { get; init; }

    public string? ClientCodeChallengeMethod { get; init; }

    public int? ExpirationMinutes { get; init; }

    public required string IdpState { get; init; }

    public required string IdpCodeVerifier { get; init; }
}

internal sealed class PortalOAuthAuthorizationCode : PortalOAuthRecord
{
    public required string ClientId { get; init; }

    // The redirect URI stored with the code must match the one presented at exchange.
    public required string RedirectUri { get; init; }

    public string? CodeChallenge { get; init; }

    public string? CodeChallengeMethod { get; init; }

    public int? ExpirationMinutes { get; init; }

    public required PortalOAuthPrincipal Principal { get; init; }
}

internal sealed class PortalOAuthRefreshToken : PortalOAuthRecord
{
    // Null binding mode keeps old refresh records on the referer-bound path and must not upgrade them to bearer.
    public PortalTokenClientType? ClientType { get; init; }

    public string? BindingValue { get; init; }

    public required string ClientId { get; init; }

    public required PortalOAuthPrincipal Principal { get; init; }

    public DateTimeOffset IssuedAt { get; init; }
}

internal sealed class PortalOAuthRefreshRevocation : PortalOAuthRecord
{
    public DateTimeOffset RevokedAt { get; init; }
}

internal sealed class PortalOAuthPrincipal
{
    public required string PrincipalId { get; init; }

    public string? DisplayName { get; init; }

    public string? TenantId { get; init; }

    public required string[] Roles { get; init; }

    public bool? RolesRequireClaimsMappingEntitlement { get; init; }

    // Null fallback roles mean unknown and stay fail-closed; an empty array is a known empty fallback.
    public string[]? RolesWithoutClaimsMapping { get; init; }

    // A missing claims-mapping flag means unknown provenance and fails closed, so a cached code cannot carry a mapping-derived tenant through a refresh.
    public bool? TenantRequiresClaimsMappingEntitlement { get; init; }

    // A present source clamps the minted token to that credential's expiry. Null identifies cached principals written before source provenance was recorded.
    public PortalCredentialSource? Source { get; init; }

    public static PortalOAuthPrincipal From(PortalCredentialPrincipal principal)
        => new()
        {
            PrincipalId = principal.PrincipalId,
            DisplayName = principal.DisplayName,
            TenantId = principal.TenantId,
            Roles = principal.Roles.ToArray(),
            RolesRequireClaimsMappingEntitlement = principal.RolesRequireClaimsMappingEntitlement,
            RolesWithoutClaimsMapping = principal.RolesWithoutClaimsMapping?.ToArray(),
            TenantRequiresClaimsMappingEntitlement =
                principal.TenantId is not null && principal.TenantRequiresClaimsMappingEntitlement,
            Source = principal.Source,
        };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PortalOAuthBrokerSession))]
[JsonSerializable(typeof(PortalOAuthAuthorizationCode))]
[JsonSerializable(typeof(PortalOAuthRefreshToken))]
[JsonSerializable(typeof(PortalOAuthRefreshRevocation))]
[JsonSerializable(typeof(PortalOAuthPrincipal))]
[JsonSerializable(typeof(PortalCredentialSource))]
internal sealed partial class PortalOAuthStoreJsonContext : JsonSerializerContext
{
}
