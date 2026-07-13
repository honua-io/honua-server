// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Distributed-cache backed store for the ArcGIS OAuth2 named-user bridge (#1242).
/// Persists three short-lived artifacts that the bridge needs across the
/// browser/IdP round trips:
/// <list type="bullet">
/// <item><description>a <em>broker</em> session that pins the ArcGIS client's
/// <c>redirect_uri</c>/<c>state</c>/PKCE challenge to the IdP PKCE verifier while
/// the user authenticates at the identity provider,</description></item>
/// <item><description>an issued ArcGIS <em>authorization code</em> bound to the
/// verified principal and the client's PKCE challenge, and</description></item>
/// <item><description>a <em>refresh token</em> bound to the verified principal so
/// long-lived ArcGIS clients can mint a fresh access token without re-prompting.</description></item>
/// </list>
/// The dual-tier (memory + distributed) pattern mirrors
/// <see cref="AdminAuthSessionStore"/> and <see cref="PortalTokenIssuer"/>: the
/// distributed tier stays authoritative for multi-node deployments and the
/// in-memory tier accelerates the redis round trip. No second user/token store is
/// introduced — these are single-use brokering artifacts that expire quickly.
/// </summary>
internal sealed partial class PortalOAuthStore(
    IMemoryCache memoryCache,
    ILogger<PortalOAuthStore> logger,
    IDistributedCache? distributedCache = null)
{
    private const string BrokerKeyPrefix = "portal-oauth:broker:";
    private const string CodeKeyPrefix = "portal-oauth:code:";
    private const string RefreshKeyPrefix = "portal-oauth:refresh:";
    // Prefix for the short-lived Redis claim keys used by AtomicConsumeAsync to ensure
    // single-use semantics under concurrent requests (BH2-022, BH2-023).
    private const string ConsumeClaimPrefix = "portal-oauth:claim:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<PortalOAuthStore> _logger = logger;
    private readonly IDistributedCache? _distributedCache = distributedCache;
    // Lock used to make TryGetValue+Remove atomic on the memory-only path (BH2-022).
    private readonly Lock _consumeLock = new();

    /// <summary>Persists a broker session and returns its opaque identifier.</summary>
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

    /// <summary>Reads and removes a broker session (single use).</summary>
    public Task<PortalOAuthBrokerSession?> ConsumeBrokerSessionAsync(
        string? id,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            BrokerKeyPrefix,
            id,
            PortalOAuthStoreJsonContext.Default.PortalOAuthBrokerSession,
            cancellationToken);

    /// <summary>Persists an issued ArcGIS authorization code and returns its value.</summary>
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

    /// <summary>
    /// Atomically reads and removes an issued ArcGIS authorization code (single use).
    /// Uses a distributed claim lock on the Redis path to prevent concurrent callers
    /// from both acquiring the record (BH2-022 fix).
    /// </summary>
    public Task<PortalOAuthAuthorizationCode?> ConsumeAuthorizationCodeAsync(
        string? code,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            CodeKeyPrefix,
            code,
            PortalOAuthStoreJsonContext.Default.PortalOAuthAuthorizationCode,
            cancellationToken);

    /// <summary>Persists a refresh token and returns its value.</summary>
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

    /// <summary>Reads a refresh token without removing it (refresh tokens are reusable until expiry).</summary>
    public Task<PortalOAuthRefreshToken?> GetRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => GetAsync(
            RefreshKeyPrefix,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    /// <summary>Removes a refresh token (used on rotation).</summary>
    public Task RemoveRefreshTokenAsync(string? token, CancellationToken cancellationToken)
        => RemoveAsync(RefreshKeyPrefix, token, cancellationToken);

    /// <summary>
    /// Atomically reads and removes a refresh token (single use when rotation is enabled).
    /// Uses a distributed claim lock on the Redis path to prevent concurrent callers
    /// from both acquiring the record (BH2-023 fix).
    /// </summary>
    public Task<PortalOAuthRefreshToken?> ConsumeRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => AtomicConsumeAsync(
            RefreshKeyPrefix,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    /// <summary>
    /// Restores a refresh token that was atomically consumed but whose subsequent
    /// token issuance failed (BH2-024 fix). The token is re-persisted under its
    /// original value with its remaining TTL so the client can retry successfully.
    /// </summary>
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
            catch (Exception ex)
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
        catch (Exception ex)
        {
            // Intentional: removal is best-effort; the entry's own TTL is the safety net,
            // and the failure is already logged for diagnosis.
            PortalOAuthLog.StoreRemoveFailed(_logger, LogValueRedactor.Hash(key), ex);
        }
    }

    /// <summary>
    /// Atomically reads and removes a single-use OAuth artifact (authorization code or
    /// rotated refresh token). RFC 6749 requires these to be single-use; a non-atomic
    /// GET+DELETE allows two concurrent callers to both acquire the record and issue
    /// independent tokens from the same credential (BH2-022, BH2-023).
    ///
    /// For the distributed (Redis) path, a short-lived SET NX claim key is used as
    /// a distributed mutex so only one concurrent caller can proceed with the GET+DEL.
    /// The claim key is managed independently via <see cref="StackExchange.Redis.IDatabase"/>
    /// (STRING type) and does not interfere with the distributed cache's HASH-based
    /// storage format. On non-Redis distributed caches (or when the IDatabase is not
    /// yet lazily initialized) the operation degrades to a non-atomic GET+DEL.
    ///
    /// For the memory-only path, a <see langword="lock"/> makes TryGetValue+Remove
    /// atomic within this process.
    /// </summary>
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
            catch (Exception ex)
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
            catch (Exception ex)
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
            catch (Exception ex)
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
                catch (Exception ex)
                {
                    // Ignored; the claim key expires automatically via its TTL.
                    PortalOAuthLog.ClaimLockReleaseFailed(_logger, LogValueRedactor.Hash(key), ex);
                }
            }
        }
    }

    /// <summary>
    /// Attempts to obtain the underlying <see cref="StackExchange.Redis.IDatabase"/> from
    /// the distributed cache when it is backed by Redis.
    /// Returns <see langword="null"/> when the distributed cache is not Redis-backed or
    /// when the IDatabase has not yet been lazily initialized (first-request cold start).
    /// </summary>
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
        catch (Exception ex)
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

/// <summary>Common expiry contract for cached OAuth brokering artifacts.</summary>
internal abstract class PortalOAuthRecord
{
    /// <summary>Absolute expiry instant of the cached artifact.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Broker session created while the user authenticates at the IdP. Pins the ArcGIS
/// client's request parameters to the Honua-initiated IdP PKCE leg.
/// </summary>
internal sealed class PortalOAuthBrokerSession : PortalOAuthRecord
{
    /// <summary>OIDC provider key chosen for this login.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>ArcGIS client identifier supplied to <c>oauth2/authorize</c>.</summary>
    public required string ClientId { get; init; }

    /// <summary>ArcGIS client redirect URI the browser is returned to.</summary>
    public required string RedirectUri { get; init; }

    /// <summary>Opaque ArcGIS-client <c>state</c> echoed back verbatim.</summary>
    public string? ClientState { get; init; }

    /// <summary>ArcGIS-client PKCE <c>code_challenge</c>, or <see langword="null"/> when not used.</summary>
    public string? ClientCodeChallenge { get; init; }

    /// <summary>ArcGIS-client PKCE <c>code_challenge_method</c> (<c>S256</c> or <c>plain</c>).</summary>
    public string? ClientCodeChallengeMethod { get; init; }

    /// <summary>Requested access-token lifetime in minutes, clamped on issuance.</summary>
    public int? ExpirationMinutes { get; init; }

    /// <summary>The CSRF <c>state</c> value Honua sends to the IdP.</summary>
    public required string IdpState { get; init; }

    /// <summary>The PKCE verifier Honua sends to the IdP token endpoint.</summary>
    public required string IdpCodeVerifier { get; init; }
}

/// <summary>Issued ArcGIS authorization code bound to a verified named user.</summary>
internal sealed class PortalOAuthAuthorizationCode : PortalOAuthRecord
{
    /// <summary>ArcGIS client identifier the code was issued to.</summary>
    public required string ClientId { get; init; }

    /// <summary>Redirect URI the code was issued for (must match on exchange).</summary>
    public required string RedirectUri { get; init; }

    /// <summary>ArcGIS-client PKCE <c>code_challenge</c>, or <see langword="null"/>.</summary>
    public string? CodeChallenge { get; init; }

    /// <summary>ArcGIS-client PKCE <c>code_challenge_method</c>.</summary>
    public string? CodeChallengeMethod { get; init; }

    /// <summary>Requested access-token lifetime in minutes, clamped on issuance.</summary>
    public int? ExpirationMinutes { get; init; }

    /// <summary>The verified principal projected from the IdP token.</summary>
    public required PortalOAuthPrincipal Principal { get; init; }
}

/// <summary>Refresh token bound to a verified named user.</summary>
internal sealed class PortalOAuthRefreshToken : PortalOAuthRecord
{
    /// <summary>ArcGIS client identifier the refresh token was issued to.</summary>
    public required string ClientId { get; init; }

    /// <summary>The verified principal projected from the IdP token.</summary>
    public required PortalOAuthPrincipal Principal { get; init; }
}

/// <summary>
/// Serializable projection of <see cref="PortalCredentialPrincipal"/> for the cache.
/// </summary>
internal sealed class PortalOAuthPrincipal
{
    /// <summary>Stable identifier of the authenticated principal.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>Display name surfaced through the issued token's principal.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Tenant scope to attach to the issued token, or <see langword="null"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Roles granted for the lifetime of issued tokens.</summary>
    public required string[] Roles { get; init; }

    /// <summary>Projects a verified credential principal into the serializable form.</summary>
    public static PortalOAuthPrincipal From(PortalCredentialPrincipal principal)
        => new()
        {
            PrincipalId = principal.PrincipalId,
            DisplayName = principal.DisplayName,
            TenantId = principal.TenantId,
            Roles = principal.Roles.ToArray(),
        };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PortalOAuthBrokerSession))]
[JsonSerializable(typeof(PortalOAuthAuthorizationCode))]
[JsonSerializable(typeof(PortalOAuthRefreshToken))]
[JsonSerializable(typeof(PortalOAuthPrincipal))]
internal sealed partial class PortalOAuthStoreJsonContext : JsonSerializerContext
{
}
