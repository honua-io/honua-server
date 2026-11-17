// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed class AdminPortalCredentialVerifier(
    IOptions<ApiKeyAuthenticationOptions> apiKeyOptions,
    IConnectionSecretResolver? secretResolver = null,
    IAdminApiKeyStore? adminApiKeyStore = null,
    ITenantContext? tenantContext = null,
    ILogger<AdminPortalCredentialVerifier>? logger = null,
    IHttpContextAccessor? httpContextAccessor = null) : IPortalCredentialVerifier
{
    private const string AdminUsername = "admin";
    private static readonly string[] AdminRoles = ["admin"];

    private readonly ApiKeyAuthenticationOptions _apiKeyOptions = apiKeyOptions?.Value ?? throw new ArgumentNullException(nameof(apiKeyOptions));
    private readonly IConnectionSecretResolver? _secretResolver = secretResolver;
    private readonly IAdminApiKeyStore? _adminApiKeyStore = adminApiKeyStore;
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ILogger<AdminPortalCredentialVerifier>? _logger = logger;

    public Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
        => VerifyAsync(new PortalCredentialAttempt(username, password, null), null, cancellationToken);

    internal async Task<PortalCredentialPrincipal?> VerifyAsync(
        PortalCredentialAttempt request,
        AuthenticationAttemptGate? gate,
        CancellationToken cancellationToken)
    {
        var (username, password, client) = request;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || gate?.RefusalFor(client) is not null)
        {
            return null;
        }

        var storedKey = await TryValidateStoredKeyAsync(password, cancellationToken).ConfigureAwait(false);
        if (storedKey is not null)
        {
            return CreateManagedKeyPrincipal(storedKey.Record);
        }

        // Reserved only after the managed-key lookup, so valid keys never contend for a slot.
        using var attempt = gate?.Begin(client, password) ?? AuthenticationAttempt.Untracked();
        if (attempt.RetryAfter is not null)
        {
            return null;
        }

        // A managed key is accepted under any username so existing clients pass through unchanged, and a
        // store outage returns no principal; only the admin username has a break-glass password. Both
        // refusals count like a wrong password, so they reveal nothing about it.
        if (!string.Equals(username, AdminUsername, StringComparison.OrdinalIgnoreCase) || !PermitsAdminPasswordFromClient())
        {
            attempt.CountFailure();
            return null;
        }

        return await VerifyAdminPasswordAsync(password, attempt, cancellationToken).ConfigureAwait(false);
    }

    // Like the API-key handler, an unresolvable or unset password is not counted: the caller cannot fix it by guessing.
    private async Task<PortalCredentialPrincipal?> VerifyAdminPasswordAsync(
        string password,
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken)
    {
        string? configuredPassword;
        try
        {
            configuredPassword = await ResolveAdminPasswordAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Secret-provider outages and rotated values that violate the production credential
            // policy are authentication failures, not unhandled endpoint errors.
            if (_logger is not null)
            {
                AuthenticationLog.AdminPasswordResolutionFailed(_logger, ex);
            }

            return null;
        }

        if (string.IsNullOrEmpty(configuredPassword))
        {
            return null;
        }

        if (!ConstantTimeEquals(password, configuredPassword))
        {
            attempt.CountFailure();
            return null;
        }

        return new PortalCredentialPrincipal(
            PrincipalId: AdminUsername,
            DisplayName: AdminUsername,
            TenantId: _tenantContext?.TenantId,
            Roles: AdminRoles,
            // Bind the token to the password value it was minted from, so rotating the
            // password (or the secret it resolves from) stops the tokens issued from the
            // previous one instead of leaving them valid for their whole lifetime (SEC-9).
            Source: new PortalCredentialSource(
                PortalCredentialSourceKind.AdminPassword,
                Reference: null,
                Version: PortalCredentialSourceVersion.ForAdminPassword(configuredPassword)));
    }

    private bool PermitsAdminPasswordFromClient()
    {
        if (AdminPasswordNetworkPolicy.Permits(_apiKeyOptions, ClientConnection.From(httpContextAccessor?.HttpContext).Reach))
        {
            return true;
        }

        if (_logger is not null)
        {
            AuthenticationLog.AdminPasswordRefusedOnNetworkPath(_logger);
        }

        return false;
    }

    private PortalCredentialPrincipal? CreateManagedKeyPrincipal(AdminApiKeyRecord record)
    {
        // This bridge mints token roles, not API-key permission, layer-write, or
        // approved-operation claims. Refuse credentials whose authority it cannot
        // preserve rather than widening scoped keys.
        var roles = ManagedKeyExchangeAuthority.ResolveTokenRoles(record);
        if (roles is null)
        {
            return null;
        }

        return new PortalCredentialPrincipal(
            PrincipalId: AdminUsername,
            DisplayName: record.Name,
            TenantId: _tenantContext?.TenantId,
            Roles: roles,
            // Record the key the token derives from, plus its expiry, so the issued token is
            // clamped to the key's own lifetime and stops the moment the key is revoked,
            // expires or is rotated (SEC-9).
            Source: new PortalCredentialSource(
                PortalCredentialSourceKind.ManagedApiKey,
                Reference: record.Id.ToString("D"),
                Version: PortalCredentialSourceVersion.ForManagedKey(record),
                ExpiresAt: record.ExpiresAt));
    }

    private async Task<AdminApiKeyValidationResult?> TryValidateStoredKeyAsync(
        string password,
        CancellationToken cancellationToken)
    {
        if (_adminApiKeyStore is null)
        {
            return null;
        }

        try
        {
            return await _adminApiKeyStore.ValidateAsync(password, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CredentialStoreFault.IsUnavailable(ex))
        {
            if (_logger is not null)
            {
                AuthenticationLog.AdminApiKeyStoreUnavailable(_logger, ex);
            }

            return null;
        }
    }

    private Task<string?> ResolveAdminPasswordAsync(CancellationToken cancellationToken)
        => PortalCredentialSourceVersion.ResolveAdminPasswordAsync(
            _apiKeyOptions,
            _secretResolver,
            cancellationToken);

    private static bool ConstantTimeEquals(string provided, string configured)
    {
        // Mirror the API-key handler's fixed-time comparison so a brute-force
        // probe cannot distinguish password mismatches from length mismatches.
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        var maxLength = Math.Max(providedBytes.Length, configuredBytes.Length);

        byte[]? providedRental = null;
        byte[]? configuredRental = null;
        Span<byte> paddedProvided = maxLength <= 256
            ? stackalloc byte[maxLength]
            : (providedRental = ArrayPool<byte>.Shared.Rent(maxLength)).AsSpan(0, maxLength);
        Span<byte> paddedConfigured = maxLength <= 256
            ? stackalloc byte[maxLength]
            : (configuredRental = ArrayPool<byte>.Shared.Rent(maxLength)).AsSpan(0, maxLength);

        paddedProvided.Clear();
        paddedConfigured.Clear();
        providedBytes.CopyTo(paddedProvided);
        configuredBytes.CopyTo(paddedConfigured);

        try
        {
            return CryptographicOperations.FixedTimeEquals(paddedProvided, paddedConfigured) &&
                   providedBytes.Length == configuredBytes.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(paddedProvided);
            CryptographicOperations.ZeroMemory(paddedConfigured);
            if (providedRental is not null)
            {
                ArrayPool<byte>.Shared.Return(providedRental, clearArray: false);
            }
            if (configuredRental is not null)
            {
                ArrayPool<byte>.Shared.Return(configuredRental, clearArray: false);
            }
        }
    }
}

internal readonly record struct PortalCredentialAttempt(string Username, string Password, IPAddress? Client);
