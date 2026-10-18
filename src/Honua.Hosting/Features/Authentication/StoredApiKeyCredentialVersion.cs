// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Identifies the exact stored credential authenticated by the server, independently
/// of its stable key ID. Rotation changes this version; ordinary last-used updates do not.
/// </summary>
internal static class StoredApiKeyCredentialVersion
{
    internal const string ClaimType = "honua:api_key_credential_version";

    /// <summary>One-way version fingerprint; never includes key material or the stored verifier itself.</summary>
    internal static string FromRecord(AdminApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(record.KeyHash))}";
    }

    /// <summary>Only an unambiguous, authenticated, framework-stamped version is accepted.</summary>
    internal static string? GetStamped(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var claims = principal.FindAll(ClaimType).ToArray();
        return claims.Length == 1 && CanonicalSecurityActor.IsFrameworkOwnedClaim(claims[0])
            && claims[0].Value.Length == 71 && claims[0].Value.StartsWith("sha256:", StringComparison.Ordinal)
            && claims[0].Value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0
                ? claims[0].Value : null;
    }
}
