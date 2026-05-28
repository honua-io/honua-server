// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal sealed partial class AdminAuthSessionStore(
    IMemoryCache memoryCache,
    ILogger<AdminAuthSessionStore> logger,
    IDistributedCache? distributedCache = null)
{
    public const string PendingSessionCookieName = "honua_admin_login";
    public const string AuthSessionCookieName = "honua_admin_session";

    private const string PendingSessionKeyPrefix = "admin-auth:pending:";
    private const string AuthSessionKeyPrefix = "admin-auth:session:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<AdminAuthSessionStore> _logger = logger;
    private readonly IDistributedCache? _distributedCache = distributedCache;

    public async Task<string> CreatePendingSessionAsync(
        string providerKey,
        string state,
        string codeVerifier,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var sessionId = CreateSessionId();
        var session = new AdminAuthPendingSession
        {
            ProviderKey = providerKey,
            State = state,
            CodeVerifier = codeVerifier,
            ExpiresAt = expiresAt
        };

        await SetAsync(
            PendingSessionKeyPrefix + sessionId,
            session,
            expiresAt,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthPendingSession,
            cancellationToken).ConfigureAwait(false);

        return sessionId;
    }

    public async Task<AdminAuthPendingSession?> GetPendingSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        return await GetAsync(
            PendingSessionKeyPrefix,
            sessionId,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthPendingSession,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePendingSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        await RemoveAsync(PendingSessionKeyPrefix, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CreateAuthenticatedSessionAsync(
        string providerKey,
        string accessToken,
        string? idToken,
        IReadOnlyList<AdminAuthSessionClaim> claims,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var sessionId = CreateSessionId();
        var session = new AdminAuthAuthenticatedSession
        {
            ProviderKey = providerKey,
            AccessToken = accessToken,
            IdToken = idToken,
            Claims = claims.ToArray(),
            ExpiresAt = expiresAt
        };

        await SetAsync(
            AuthSessionKeyPrefix + sessionId,
            session,
            expiresAt,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthAuthenticatedSession,
            cancellationToken).ConfigureAwait(false);

        return sessionId;
    }

    public async Task<AdminAuthAuthenticatedSession?> GetAuthenticatedSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        return await GetAsync(
            AuthSessionKeyPrefix,
            sessionId,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthAuthenticatedSession,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAuthenticatedSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        await RemoveAsync(AuthSessionKeyPrefix, sessionId, cancellationToken).ConfigureAwait(false);
    }

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
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expiresAt
                },
                cancellationToken).ConfigureAwait(false);
            _memoryCache.Set(key, payload, expiresAt);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(key);
            AdminAuthSessionLog.DistributedCachePersistFailed(_logger, GetKeyFamily(key), LogValueRedactor.Hash(key), ex);
            // Surface the failure to the caller so the client retries instead of
            // continuing with a single-instance-only session that breaks the moment
            // another replica serves the next request.
            throw;
        }
    }

    private async Task<T?> GetAsync<T>(
        string keyPrefix,
        string? sessionId,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : AdminAuthSessionState
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var key = keyPrefix + sessionId;
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
                _memoryCache.Remove(key);
                AdminAuthSessionLog.DistributedCacheReadFailed(_logger, GetKeyFamily(key), LogValueRedactor.Hash(key), ex);
                return null;
            }
        }

        else if (!_memoryCache.TryGetValue(key, out payload))
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(payload, typeInfo);
        if (value is null)
        {
            await RemoveAsync(keyPrefix, sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RemoveAsync(keyPrefix, sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        _memoryCache.Set(key, payload, value.ExpiresAt);
        return value;
    }

    private async Task RemoveAsync(string keyPrefix, string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var key = keyPrefix + sessionId;
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
            AdminAuthSessionLog.DistributedCacheRemoveFailed(_logger, GetKeyFamily(key), LogValueRedactor.Hash(key), ex);
        }
    }

    private static string GetKeyFamily(string key)
    {
        if (key.StartsWith(PendingSessionKeyPrefix, StringComparison.Ordinal))
        {
            return "admin-auth:pending";
        }

        return key.StartsWith(AuthSessionKeyPrefix, StringComparison.Ordinal)
            ? "admin-auth:session"
            : "admin-auth:unknown";
    }

    private static string CreateSessionId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

}

internal abstract class AdminAuthSessionState
{
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class AdminAuthPendingSession : AdminAuthSessionState
{
    public required string ProviderKey { get; init; }

    public required string State { get; init; }

    public required string CodeVerifier { get; init; }
}

internal sealed class AdminAuthAuthenticatedSession : AdminAuthSessionState
{
    public required string ProviderKey { get; init; }

    public required string AccessToken { get; init; }

    public string? IdToken { get; init; }

    public required AdminAuthSessionClaim[] Claims { get; init; }
}

internal sealed class AdminAuthSessionClaim
{
    public required string Type { get; init; }

    public required string Value { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdminAuthPendingSession))]
[JsonSerializable(typeof(AdminAuthAuthenticatedSession))]
[JsonSerializable(typeof(AdminAuthSessionClaim))]
[JsonSerializable(typeof(AdminAuthSessionClaim[]))]
internal sealed partial class AdminAuthSessionStoreJsonContext : JsonSerializerContext
{
}
