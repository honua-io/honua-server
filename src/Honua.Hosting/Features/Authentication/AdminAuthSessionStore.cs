// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Infrastructure.Security;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Infrastructure.Authentication;

// Sessions live in the shared token store, so a sign-in on one node is honoured on every node and a
// sign-out is effective on the next request anywhere.
internal sealed partial class AdminAuthSessionStore(
    ISecurityTokenStore tokens,
    ILogger<AdminAuthSessionStore> logger)
{
    public const string PendingSessionCookieName = "honua_admin_login";
    public const string AuthSessionCookieName = "honua_admin_session";

    private const string PendingSessionFamily = "pending";
    private const string AuthSessionFamily = "session";

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
            new SecurityTokenKey(SecurityTokenKind.AdminPendingSignIn, sessionId),
            StoredSession.Of(session, AdminAuthSessionStoreJsonContext.Default.AdminAuthPendingSession, expiresAt),
            cancellationToken).ConfigureAwait(false);

        return sessionId;
    }

    public async Task<AdminAuthPendingSession?> GetPendingSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        return await GetAsync(
            SecurityTokenKind.AdminPendingSignIn,
            sessionId,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthPendingSession,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePendingSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            await tokens.RevokeAsync(new SecurityTokenKey(SecurityTokenKind.AdminPendingSignIn, sessionId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            // A pending sign-in that could not be removed expires on its own within minutes.
            AdminAuthSessionLog.DistributedCacheRemoveFailed(logger, PendingSessionFamily, LogValueRedactor.Hash(sessionId), storeFailure);
        }
    }

    public Task<string> CreateAuthenticatedSessionAsync(
        string providerKey,
        string accessToken,
        string? idToken,
        IReadOnlyList<AdminAuthSessionClaim> claims,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
        => CreateAuthenticatedSessionAsync(new AdminAuthAuthenticatedSession
        {
            ProviderKey = providerKey,
            AccessToken = accessToken,
            IdToken = idToken,
            Claims = claims.ToArray(),
            ExpiresAt = expiresAt
        }, cancellationToken);

    public async Task<string> CreateAuthenticatedSessionAsync(
        AdminAuthAuthenticatedSession session, CancellationToken cancellationToken)
    {
        var sessionId = CreateSessionId();

        await SetAsync(
            new SecurityTokenKey(SecurityTokenKind.AdminSession, sessionId),
            StoredSession.Of(session, AdminAuthSessionStoreJsonContext.Default.AdminAuthAuthenticatedSession, session.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        return sessionId;
    }

    public async Task<AdminAuthAuthenticatedSession?> GetAuthenticatedSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        return await GetAsync(
            SecurityTokenKind.AdminSession,
            sessionId,
            AdminAuthSessionStoreJsonContext.Default.AdminAuthAuthenticatedSession,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAuthenticatedSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            await tokens.RevokeAsync(new SecurityTokenKey(SecurityTokenKind.AdminSession, sessionId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            // A sign-out that cannot be confirmed must not look like success.
            AdminAuthSessionLog.DistributedCacheRemoveFailed(logger, AuthSessionFamily, LogValueRedactor.Hash(sessionId), storeFailure);
            throw new ServiceUnavailableException("The shared admin session could not be revoked.", storeFailure);
        }
    }

    // An approval needs proof of a human sign-in, so a session that cannot be read is an outage, not an absence.
    public async Task<AdminAuthAuthenticatedSession?> GetInteractiveSessionForApprovalAsync(
        string? sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await GetAuthenticatedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return session?.Origin == AdminAuthSessionOrigin.InteractiveOidc ? session : null;
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new ServiceUnavailableException("The shared human session could not be verified.", storeFailure);
        }
    }

    // A session another node will not see must not look created, so a store failure reaches the caller.
    private Task SetAsync(SecurityTokenKey key, StoredSession session, CancellationToken cancellationToken)
        => tokens.StoreAsync(key, session.Payload, session.ExpiresAt - DateTimeOffset.UtcNow, cancellationToken);

    private readonly record struct StoredSession(byte[] Payload, DateTimeOffset ExpiresAt)
    {
        public static StoredSession Of<T>(T value, JsonTypeInfo<T> typeInfo, DateTimeOffset expiresAt)
            => new(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), expiresAt);
    }

    private async Task<T?> GetAsync<T>(
        SecurityTokenKind kind,
        string? sessionId,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : AdminAuthSessionState
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var stored = await tokens.FindAsync(new SecurityTokenKey(kind, sessionId), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var session = JsonSerializer.Deserialize(stored.Payload, typeInfo);
        return session is null || session.ExpiresAt <= DateTimeOffset.UtcNow ? null : session;
    }

    private static string CreateSessionId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

}

internal enum AdminAuthSessionOrigin
{
    Unknown,
    InteractiveOidc
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
    public AdminAuthSessionOrigin Origin { get; init; }

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
