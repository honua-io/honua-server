// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Default <see cref="IPortalCredentialVerifier"/> bridging to the local admin
/// password and admin API-key store. Used when no custom verifier is registered.
/// </summary>
internal sealed class AdminPortalCredentialVerifier(
    IOptions<ApiKeyAuthenticationOptions> apiKeyOptions,
    IConnectionSecretResolver? secretResolver = null,
    IAdminApiKeyStore? adminApiKeyStore = null,
    ITenantContext? tenantContext = null,
    ILogger<AdminPortalCredentialVerifier>? logger = null) : IPortalCredentialVerifier
{
    private const string AdminUsername = "admin";
    private static readonly string[] AdminRoles = ["admin"];

    private readonly ApiKeyAuthenticationOptions _apiKeyOptions = apiKeyOptions?.Value ?? throw new ArgumentNullException(nameof(apiKeyOptions));
    private readonly IConnectionSecretResolver? _secretResolver = secretResolver;
    private readonly IAdminApiKeyStore? _adminApiKeyStore = adminApiKeyStore;
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ILogger<AdminPortalCredentialVerifier>? _logger = logger;

    /// <inheritdoc />
    public async Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        if (!string.Equals(username, AdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            // Allow operators to authenticate with an admin API key while supplying
            // the API key as the password under any username so existing clients can
            // pass through without code changes.
            if (_adminApiKeyStore is not null)
            {
                var apiKeyResult = await _adminApiKeyStore.ValidateAsync(password, cancellationToken).ConfigureAwait(false);
                if (apiKeyResult is not null)
                {
                    return new PortalCredentialPrincipal(
                        PrincipalId: AdminUsername,
                        DisplayName: apiKeyResult.Record.Name,
                        TenantId: _tenantContext?.TenantId,
                        Roles: AdminRoles);
                }
            }

            return null;
        }

        if (_adminApiKeyStore is not null)
        {
            var apiKeyResult = await _adminApiKeyStore.ValidateAsync(password, cancellationToken).ConfigureAwait(false);
            if (apiKeyResult is not null)
            {
                return new PortalCredentialPrincipal(
                    PrincipalId: AdminUsername,
                    DisplayName: apiKeyResult.Record.Name,
                    TenantId: _tenantContext?.TenantId,
                    Roles: AdminRoles);
            }
        }

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
            return null;
        }

        return new PortalCredentialPrincipal(
            PrincipalId: AdminUsername,
            DisplayName: AdminUsername,
            TenantId: _tenantContext?.TenantId,
            Roles: AdminRoles);
    }

    private async Task<string?> ResolveAdminPasswordAsync(CancellationToken cancellationToken)
    {
        var configuredPassword = _apiKeyOptions.AdminPassword;
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return null;
        }

        var resolvedPassword = configuredPassword;
        if (_secretResolver is not null)
        {
            var canResolve = await _secretResolver.CanResolveSecretAsync(configuredPassword, cancellationToken).ConfigureAwait(false);
            if (canResolve)
            {
                resolvedPassword = await _secretResolver.ResolveConnectionStringAsync(configuredPassword, cancellationToken).ConfigureAwait(false);
            }
        }

        AdminPasswordValidation.ValidateRefreshedPassword(resolvedPassword, _apiKeyOptions.EnvironmentName);
        return resolvedPassword;
    }

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
