// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Security;

/// <summary>
/// Framework-owned provenance for the validated upstream subject protocol.
/// </summary>
/// <remarks>
/// Provider claims such as <c>auth_type</c> are not a trust boundary: an issuer may emit a
/// claim with that name. Authentication projections remove any inbound copy of this private
/// claim and stamp exactly one value after OIDC or SAML validation. Durable identity consumers
/// use only this provenance when deciding whether an issuer is required.
/// </remarks>
public static class IdentityProtocolProvenance
{
    /// <summary>The private claim carrying the validated upstream protocol.</summary>
    public const string ClaimType = "honua_identity_protocol";

    /// <summary>Canonical OpenID Connect protocol value.</summary>
    public const string Oidc = "oidc";

    /// <summary>Canonical SAML protocol value.</summary>
    public const string Saml = "saml";

    /// <summary>
    /// Resolves one unambiguous, supported protocol from a projected principal.
    /// Missing, conflicting, or unsupported provenance fails closed.
    /// </summary>
    public static string? Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var protocols = principal.FindAll(ClaimType)
            .Select(static claim => Normalize(claim.Value))
            .Where(static value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();

        return protocols.Length == 1 && IsSupported(protocols[0])
            ? protocols[0]
            : null;
    }

    /// <summary>Returns a canonical supported protocol value, or <see langword="null"/>.</summary>
    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>Returns whether a canonical value is a supported durable subject protocol.</summary>
    public static bool IsSupported(string? value)
        => value is Oidc or Saml;
}
