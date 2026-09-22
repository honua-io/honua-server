// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Builds the non-reversible version markers that bind a portal token to the credential it
/// was minted from (SEC-9), and resolves the configured admin password that the marker for
/// <see cref="Honua.Core.Features.Authorization.Abstractions.PortalCredentialSourceKind.AdminPassword"/>
/// is derived from.
/// </summary>
/// <remarks>
/// A marker only has to change when the credential changes. No credential material is stored:
/// the managed-key marker is a labelled SHA-256 of the key record's stored hash (itself a
/// SHA-256 of 256 random bits), and the password marker is a PBKDF2 derivation of the password,
/// because an operator-chosen password does not carry the entropy that makes a single fast
/// digest safe to persist. The derivation for the current password value is memoised in
/// process, so the cost is paid once per password value rather than once per request.
/// </remarks>
internal static class PortalCredentialSourceVersion
{
    private const string ManagedKeyLabel = "honua.portal-token.source.managed-key.v1|";
    private const string PasswordSalt = "honua.portal-token.source.admin-password.v1";
    private const int PasswordIterations = 100_000;

    // 128 bits is far beyond what distinguishing two credential values needs, and keeps the
    // record small enough that it costs nothing on the validation path.
    private const int MarkerHexLength = 32;

    private static PasswordMarker? _lastPasswordMarker;

    /// <summary>
    /// Marker for a managed admin API key. Rotation replaces the key material, hence the
    /// stored hash, hence this marker, so tokens minted from the pre-rotation material stop
    /// validating even though the key identifier is unchanged.
    /// </summary>
    public static string ForManagedKey(AdminApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var digest = SHA256.HashData(
            [.. Encoding.UTF8.GetBytes(ManagedKeyLabel), .. record.KeyHash]);
        return Convert.ToHexStringLower(digest)[..MarkerHexLength];
    }

    /// <summary>
    /// Marker for the configured admin password. Rotating the password (or the secret it is
    /// resolved from) changes the marker, so tokens minted from the previous value stop.
    /// </summary>
    public static string ForAdminPassword(string resolvedPassword)
    {
        ArgumentNullException.ThrowIfNull(resolvedPassword);

        var last = Volatile.Read(ref _lastPasswordMarker);
        if (last is not null && string.Equals(last.Password, resolvedPassword, StringComparison.Ordinal))
        {
            return last.Marker;
        }

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(resolvedPassword),
            Encoding.UTF8.GetBytes(PasswordSalt),
            PasswordIterations,
            HashAlgorithmName.SHA256,
            MarkerHexLength / 2);
        var marker = Convert.ToHexStringLower(derived);
        Volatile.Write(ref _lastPasswordMarker, new PasswordMarker(resolvedPassword, marker));
        return marker;
    }

    /// <summary>
    /// Resolves the configured admin password through the optional secret resolver and applies
    /// the production credential policy, exactly as the credential bridge does at verification
    /// time. Returns <see langword="null"/> when no password is configured; throws when the
    /// resolved value violates the policy, which callers treat as "cannot confirm".
    /// </summary>
    public static async Task<string?> ResolveAdminPasswordAsync(
        ApiKeyAuthenticationOptions options,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredPassword = options.AdminPassword;
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return null;
        }

        var resolvedPassword = configuredPassword;
        if (secretResolver is not null)
        {
            var canResolve = await secretResolver
                .CanResolveSecretAsync(configuredPassword, cancellationToken).ConfigureAwait(false);
            if (canResolve)
            {
                resolvedPassword = await secretResolver
                    .ResolveConnectionStringAsync(configuredPassword, cancellationToken).ConfigureAwait(false);
            }
        }

        AdminPasswordValidation.ValidateRefreshedPassword(resolvedPassword, options.EnvironmentName);
        return resolvedPassword;
    }

    private sealed record PasswordMarker(string Password, string Marker);
}
