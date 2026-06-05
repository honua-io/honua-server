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
}
