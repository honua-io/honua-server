// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Configuration options for the ArcGIS-compatible <c>/sharing/rest/generateToken</c>
/// endpoint and the matching <c>PortalToken</c> authentication scheme.
/// </summary>
public sealed class PortalTokenAuthenticationOptions
{
    /// <summary>
    /// Configuration section binding root.
    /// </summary>
    public const string SectionName = "Authentication:PortalToken";

    /// <summary>
    /// Default token lifetime when the caller does not request an explicit expiration.
    /// </summary>
    public const int DefaultExpirationMinutesValue = 60;

    /// <summary>
    /// Default maximum token lifetime (10 days), matching the ArcGIS Portal default.
    /// </summary>
    public const int DefaultMaxExpirationMinutesValue = 14_400;

    /// <summary>
    /// Whether the portal-token endpoint and scheme are wired into the pipeline.
    /// Defaults to <see langword="true"/>; operators can opt out by setting the
    /// configuration value to <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether token issuance is restricted to HTTPS requests. Defaults to
    /// <see langword="true"/>. Operators may opt out only for development /
    /// test fixtures.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Default token lifetime in minutes when the caller does not supply
    /// <c>expiration</c>.
    /// </summary>
    public int DefaultExpirationMinutes { get; set; } = DefaultExpirationMinutesValue;

    /// <summary>
    /// Upper bound on the lifetime a caller may request via <c>expiration</c>.
    /// Requests above this value are clamped to the maximum.
    /// </summary>
    public int MaxExpirationMinutes { get; set; } = DefaultMaxExpirationMinutesValue;

    /// <summary>
    /// Hardening options for the ArcGIS OAuth2 named-user bridge
    /// (<c>/sharing/rest/oauth2/{authorize,callback,token}</c>, #1242/#1484).
    /// </summary>
    public PortalOAuth2Options OAuth2 { get; set; } = new();
}

/// <summary>
/// Security-hardening options for the ArcGIS OAuth2 named-user bridge (#1484):
/// the per-deployment <c>redirect_uri</c> allow-list (open-redirect mitigation)
/// and the hard PKCE requirement toggle.
/// </summary>
public sealed class PortalOAuth2Options
{
    /// <summary>
    /// Configuration section binding root, relative to
    /// <see cref="PortalTokenAuthenticationOptions.SectionName"/>.
    /// </summary>
    public const string SectionName = "OAuth2";

    /// <summary>
    /// Per-deployment allow-list of redirect URIs the <c>oauth2/authorize</c>
    /// endpoint will accept (#1484). Each entry is either an exact absolute URI or
    /// an <em>origin</em> (scheme + host + optional port, with no path beyond
    /// <c>/</c>); an origin entry matches any redirect URI sharing that exact
    /// scheme/host/port. The ArcGIS Pro native loopback redirect
    /// (<c>urn:ietf:wg:oauth:2.0:oob</c>) is only accepted when listed verbatim.
    /// When the list is empty the authorize endpoint rejects every redirect URI,
    /// so the bridge cannot be exploited as an open redirector before an operator
    /// has explicitly registered its trusted clients.
    /// </summary>
    public string[] AllowedRedirectUris { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/> (the default) every authorization-code flow
    /// must carry a PKCE <c>code_challenge</c> at <c>authorize</c> time and a
    /// matching <c>code_verifier</c> at <c>token</c> time (#1484). Set to
    /// <see langword="false"/> only for a legacy client that genuinely cannot send
    /// PKCE; doing so re-opens the authorization-code interception window.
    /// </summary>
    public bool RequirePkce { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (the default) a successful <c>refresh_token</c>
    /// grant rotates the refresh token: the presented token is revoked and a new one
    /// is returned in the response (#1484). Rotation bounds the replay window of a
    /// leaked refresh token to a single use. Set to <see langword="false"/> to
    /// restore the non-rotating behavior for clients that cannot persist a
    /// refreshed token.
    /// </summary>
    public bool RotateRefreshTokens { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> the <c>oauth2/token</c> endpoint accepts the
    /// <c>client_credentials</c> grant for service-to-service clients (ADR-0053,
    /// #1860). The presented <c>client_secret</c> is validated against the existing
    /// Admin API-key store and an opaque, IP-bound portal access token is minted via
    /// the shared portal-token issuer. Defaults to <see langword="false"/>: with the
    /// flag off the grant is rejected with <c>unsupported_grant_type</c>, exactly as
    /// before, so no existing deployment gains a new credential path implicitly.
    /// </summary>
    public bool EnableClientCredentials { get; set; }

    /// <summary>
    /// Optional JWT access-token format and RFC 7662 introspection options
    /// (ADR-0054, #1890). Off by default: the opaque, cache-backed token path is
    /// unchanged unless an operator opts in.
    /// </summary>
    public PortalOAuthJwtOptions Jwt { get; set; } = new();

    /// <summary>
    /// Optional pluggable IdP/OIDC federation for the <c>client_credentials</c>
    /// grant (ADR-0053 Increment 3, #1889). Off by default.
    /// </summary>
    public PortalOAuthClientCredentialsFederationOptions ClientCredentialsFederation { get; set; } = new();
}

/// <summary>
/// Optional JWT access-token format and RFC 7662 introspection options
/// (ADR-0054, #1890). The default opaque, cache-backed portal-token path is
/// unchanged; JWT issuance is strictly opt-in and additive.
/// </summary>
public sealed class PortalOAuthJwtOptions
{
    /// <summary>
    /// Configuration section binding root, relative to
    /// <see cref="PortalOAuth2Options.SectionName"/>.
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// Default JWT issuer (the <c>iss</c> claim) when the operator does not override
    /// it. Used for both minting and validation.
    /// </summary>
    public const string DefaultIssuer = "https://honua.local/sharing";

    /// <summary>
    /// When <see langword="true"/> the OAuth2 token endpoint mints a signed JWT
    /// access token instead of the opaque portal token. The JWT is still recorded in
    /// the distributed cache by its <c>jti</c> so cache-eviction revocation and the
    /// single request-path validator (ADR-0049/0053) keep working. Defaults to
    /// <see langword="false"/>: with the flag off every token is the opaque format,
    /// byte-for-byte the prior behaviour.
    /// </summary>
    public bool EnableJwtAccessTokens { get; set; }

    /// <summary>
    /// When <see langword="true"/> the RFC 7662 introspection endpoint
    /// (<c>POST /sharing/rest/oauth2/introspect</c>) is wired and answers for both
    /// opaque and JWT access tokens. Defaults to <see langword="false"/>: with the
    /// flag off the endpoint returns 404, so it is never a silent surface.
    /// </summary>
    public bool EnableIntrospection { get; set; }

    /// <summary>
    /// Symmetric HMAC-SHA256 signing secret for JWT access tokens. Required (and must
    /// be at least 32 bytes) when <see cref="EnableJwtAccessTokens"/> is set. Supports
    /// <c>env:</c> indirection through the standard secret-resolution helper.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>JWT issuer (<c>iss</c>). Defaults to <see cref="DefaultIssuer"/>.</summary>
    public string Issuer { get; set; } = DefaultIssuer;

    /// <summary>
    /// Optional JWT audience (<c>aud</c>). When set it is stamped on minted tokens
    /// and required on validation.
    /// </summary>
    public string? Audience { get; set; }
}

/// <summary>
/// Optional pluggable IdP/OIDC federation for the OAuth2 <c>client_credentials</c>
/// grant (ADR-0053 Increment 3, #1889). When enabled, the presented
/// <c>client_id</c>/<c>client_secret</c> are delegated to an external OIDC token
/// endpoint (the operator's centralised machine-identity IdP) rather than the
/// in-tree client registry / Admin API-key store. On a successful federated
/// exchange Honua mints its own opaque (or JWT) portal access token bound to the
/// caller IP, carrying roles mapped from the federated response — there is no
/// parallel token store (ADR-0049). Off by default and composes only when
/// <see cref="PortalOAuth2Options.EnableClientCredentials"/> is also set.
/// </summary>
public sealed class PortalOAuthClientCredentialsFederationOptions
{
    /// <summary>
    /// Configuration section binding root, relative to
    /// <see cref="PortalOAuth2Options.SectionName"/>.
    /// </summary>
    public const string SectionName = "ClientCredentialsFederation";

    /// <summary>
    /// When <see langword="true"/> the <c>client_credentials</c> grant is delegated to
    /// the external token endpoint before falling back to the in-tree client registry /
    /// API-key store. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute URL of the external IdP OAuth2 token endpoint that the presented
    /// credentials are forwarded to with <c>grant_type=client_credentials</c>.
    /// Required when <see cref="Enabled"/> is set.
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Optional space- or comma-delimited scope forwarded to the external token
    /// endpoint when the caller does not request one.
    /// </summary>
    public string? DefaultScope { get; set; }

    /// <summary>
    /// Roles granted to the minted Honua token when the federated exchange succeeds.
    /// The external IdP is trusted to have authenticated the machine identity; these
    /// roles are the local RBAC projection of that trust. Empty grants no roles
    /// (the federated client gets a token with no privileges — never an escalation).
    /// </summary>
    public string[] GrantedRoles { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/> (the default) the federated token endpoint must be
    /// reached over HTTPS, so the forwarded <c>client_secret</c> is never sent in
    /// plaintext. Set to <see langword="false"/> only for a trusted in-cluster test IdP.
    /// </summary>
    public bool RequireHttps { get; set; } = true;
}
