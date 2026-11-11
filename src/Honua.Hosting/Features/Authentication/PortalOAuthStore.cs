// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class PortalOAuthStore(
    ISecurityTokenStore tokens,
    ILogger<PortalOAuthStore> logger)
{
    public async Task<string> CreateBrokerSessionAsync(
        PortalOAuthBrokerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = CreateValue();
        await SetAsync(
            new SecurityTokenKey(SecurityTokenKind.PortalBrokerSession, id),
            session,
            PortalOAuthStoreJsonContext.Default.PortalOAuthBrokerSession,
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public Task<PortalOAuthBrokerSession?> ConsumeBrokerSessionAsync(
        string? id,
        CancellationToken cancellationToken)
        => ConsumeAsync(
            SecurityTokenKind.PortalBrokerSession,
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
            new SecurityTokenKey(SecurityTokenKind.PortalAuthorizationCode, value),
            code,
            PortalOAuthStoreJsonContext.Default.PortalOAuthAuthorizationCode,
            cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<PortalOAuthAuthorizationCode?> ConsumeAuthorizationCodeAsync(
        string? code,
        CancellationToken cancellationToken)
        => ConsumeAsync(
            SecurityTokenKind.PortalAuthorizationCode,
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
            new SecurityTokenKey(SecurityTokenKind.PortalPrincipalRevocation, principalKey.Trim()),
            marker,
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
            SecurityTokenKind.PortalPrincipalRevocation,
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
            new SecurityTokenKey(SecurityTokenKind.PortalRefreshToken, value),
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<PortalOAuthRefreshToken?> GetRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => GetAsync(
            SecurityTokenKind.PortalRefreshToken,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    public async Task RemoveRefreshTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var key = new SecurityTokenKey(SecurityTokenKind.PortalRefreshToken, token);
        try
        {
            await tokens.RevokeAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            // Removal is best effort; the token's own expiry is the safety net.
            PortalOAuthLog.StoreRemoveFailed(logger, LogValueRedactor.Hash(token), storeFailure);
        }
    }

    public Task<PortalOAuthRefreshToken?> ConsumeRefreshTokenAsync(
        string? token,
        CancellationToken cancellationToken)
        => ConsumeAsync(
            SecurityTokenKind.PortalRefreshToken,
            token,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    // A refresh token consumed before issuance failed is restored under its original value with the remaining lifetime so the client can retry.
    public Task RestoreRefreshTokenAsync(
        string tokenValue,
        PortalOAuthRefreshToken record,
        CancellationToken cancellationToken)
        => SetAsync(
            new SecurityTokenKey(SecurityTokenKind.PortalRefreshToken, tokenValue),
            record,
            PortalOAuthStoreJsonContext.Default.PortalOAuthRefreshToken,
            cancellationToken);

    // A credential another node will not see must not look issued, so a store failure reaches the caller.
    private async Task SetAsync<T>(
        SecurityTokenKey key,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : PortalOAuthRecord
    {
        try
        {
            await tokens.StoreAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(value, typeInfo),
                value.ExpiresAt - DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            PortalOAuthLog.StorePersistFailed(logger, LogValueRedactor.Hash(key.Value), storeFailure);
            throw;
        }
    }

    // An unreadable store reads as an absent credential, which refuses the exchange.
    private async Task<T?> GetAsync<T>(
        SecurityTokenKind kind,
        string? id,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : PortalOAuthRecord
        => string.IsNullOrWhiteSpace(id)
            ? null
            : await ReadAsync(id, () => tokens.FindAsync(new SecurityTokenKey(kind, id), cancellationToken), typeInfo)
                .ConfigureAwait(false);

    // An authorization code or rotated refresh token is taken in one statement, so two callers cannot both mint from it.
    private async Task<T?> ConsumeAsync<T>(
        SecurityTokenKind kind,
        string? id,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : PortalOAuthRecord
        => string.IsNullOrWhiteSpace(id)
            ? null
            : await ReadAsync(id, () => tokens.ConsumeAsync(new SecurityTokenKey(kind, id), cancellationToken), typeInfo)
                .ConfigureAwait(false);

    private async Task<T?> ReadAsync<T>(string id, Func<Task<StoredSecurityToken?>> read, JsonTypeInfo<T> typeInfo)
        where T : PortalOAuthRecord
    {
        StoredSecurityToken? stored;
        try
        {
            stored = await read().ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            PortalOAuthLog.StoreReadFailed(logger, LogValueRedactor.Hash(id), storeFailure);
            return null;
        }

        if (stored is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(stored.Payload, typeInfo);
        return value is null || value.ExpiresAt <= DateTimeOffset.UtcNow ? null : value;
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

    public bool RolesDependOnlyOnRoleClaimSelection { get; init; }

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
            RolesDependOnlyOnRoleClaimSelection = principal.RolesDependOnlyOnRoleClaimSelection,
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
