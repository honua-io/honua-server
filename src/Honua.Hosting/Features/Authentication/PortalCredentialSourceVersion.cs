// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Infrastructure.Authentication;

// No credential material is stored: a managed-key marker is a labelled SHA-256 of the stored hash, and a password marker is PBKDF2 because a low-entropy password is not safe as one fast digest.
internal static class PortalCredentialSourceVersion
{
    private const string ManagedKeyLabel = "honua.portal-token.source.managed-key.v1|";
    private const string PasswordSalt = "honua.portal-token.source.admin-password.v1";
    private const int PasswordIterations = 100_000;

    // 128 bits is far beyond what distinguishing two credential values needs, and keeps the
    // record small enough that it costs nothing on the validation path.
    private const int MarkerHexLength = 32;

    private static PasswordMarker? _lastPasswordMarker;

    public static string ForManagedKey(AdminApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var digest = SHA256.HashData(
            [.. Encoding.UTF8.GetBytes(ManagedKeyLabel), .. record.KeyHash]);
        return Convert.ToHexStringLower(digest)[..MarkerHexLength];
    }

    // Rotating the password changes the marker, so a token minted from the old password does not match.
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

    // The resolver returns null when no password is configured and throws on a policy violation, which callers treat as cannot-confirm.
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
