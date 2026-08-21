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

/// <summary>
/// Exact authentication-handler identities that may establish durable non-federated actors.
/// </summary>
/// <remarks>
/// These values are framework-owned <see cref="ClaimsIdentity.AuthenticationType"/> values,
/// not provider-controlled claims. Keeping the mapping in Core gives Studio and the hosting
/// control plane one collision-proof trust boundary.
/// </remarks>
public static class FrameworkAuthenticationIdentity
{
    /// <summary>Private credential-kind claim stamped only by framework handlers.</summary>
    public const string CredentialKindClaimType = "honua_credential_kind";

    /// <summary>Credential-kind value for immutable stored API keys.</summary>
    public const string ApiKeyCredentialKind = "api-key";

    /// <summary>The platform API-key handler.</summary>
    public const string ApiKeyAuthenticationType = "ApiKey";

    /// <summary>The validated mutual-TLS client-certificate handler.</summary>
    public const string ClientCertificateAuthenticationType = "HonuaClientCertificate";

    /// <summary>The validated ArcGIS-compatible portal-token handler.</summary>
    public const string PortalTokenAuthenticationType = "PortalToken";

    /// <summary>The validated attenuated background-job token handler.</summary>
    public const string ScopedJobTokenAuthenticationType = "ScopedJobToken";

    /// <summary>The Honua-signed wrapper for validated admin sessions.</summary>
    public const string OperatorBearerAuthenticationType = "OperatorBearer";

    /// <summary>Authentication type used only for restored, server-captured job contexts.</summary>
    public const string JobSecurityContextAuthenticationType = "HonuaJobSecurityContext";

    /// <summary>Returns whether the identity was created by the API-key handler.</summary>
    public static bool IsApiKey(string? authenticationType)
        => string.Equals(
            authenticationType,
            ApiKeyAuthenticationType,
            StringComparison.Ordinal);

    /// <summary>
    /// Returns whether a principal carries exactly one framework API-key credential marker.
    /// </summary>
    public static bool HasApiKeyCredentialKind(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var kinds = principal.FindAll(CredentialKindClaimType)
            .Select(static claim => claim.Value?.Trim().ToLowerInvariant())
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return kinds.Length == 1
            && string.Equals(kinds[0], ApiKeyCredentialKind, StringComparison.Ordinal);
    }

    /// <summary>
    /// Maps exact framework handlers to stable durable subject namespaces.
    /// </summary>
    public static string? ResolveDurableSubjectScheme(string? authenticationType)
        => authenticationType switch
        {
            ClientCertificateAuthenticationType => "client-certificate",
            PortalTokenAuthenticationType => "portal-token",
            ScopedJobTokenAuthenticationType => "scoped-job-token",
            _ => null,
        };

    /// <summary>Returns whether a scheme is a framework-owned durable subject namespace.</summary>
    public static bool IsDurableSubjectScheme(string? scheme)
        => scheme is "client-certificate" or "portal-token" or "scoped-job-token";
}
