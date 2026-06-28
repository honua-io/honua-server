// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Options for the console-consumable operator bearer (#2258, Option C). After an
/// operator authenticates through the existing admin OIDC flow
/// (<c>AdminAuthEndpoints</c>, cookie-session bound), the console can exchange its
/// session for a short-lived, Honua-signed bearer that is forwardable to the admin
/// control-plane API as <c>Authorization: Bearer</c> and resolves to the same RBAC
/// as the cookie session.
/// </summary>
/// <remarks>
/// The bearer is a separate, bounded credential — not the upstream IdP access token,
/// which never leaves the server. Issuance and validation are fail-closed: with no
/// usable signing key the feature is disabled, the issue endpoint returns 503, and
/// the request-path validator yields no principal.
/// </remarks>
internal sealed class OperatorBearerOptions
{
    /// <summary>Configuration section that binds these options.</summary>
    public const string SectionName = "Authentication:OperatorBearer";

    internal const int MinimumSigningKeyBytes = 32;
    internal const int DefaultMaxLifetimeMinutes = 30;
    internal const int MaxAllowedLifetimeMinutes = 720;
    internal const string DefaultIssuer = "honua-operator-bearer";
    internal const string DefaultAudience = "honua-admin-api";

    /// <summary>Whether operator bearer issuance and validation are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// HMAC-SHA256 signing key (minimum 32 bytes). Supports the shared
    /// <c>env:NAME</c> indirection so the key can be sourced from an environment
    /// variable rather than persisted in configuration.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>Token issuer (<c>iss</c>); also used to route the bearer on the request path.</summary>
    public string Issuer { get; set; } = DefaultIssuer;

    /// <summary>Token audience (<c>aud</c>).</summary>
    public string Audience { get; set; } = DefaultAudience;

    /// <summary>
    /// Maximum bearer lifetime in minutes. The effective expiry is clamped to the
    /// earlier of this ceiling and the issuing admin session's own expiry so a
    /// forwardable bearer never outlives the operator's session.
    /// </summary>
    public int MaxLifetimeMinutes { get; set; } = DefaultMaxLifetimeMinutes;

    /// <summary>
    /// Whether the options are usable: enabled and backed by a signing key of at
    /// least <see cref="MinimumSigningKeyBytes"/> bytes once resolved.
    /// </summary>
    public bool IsUsable => Enabled && ResolveKeyBytes().Length >= MinimumSigningKeyBytes;

    internal string ResolveIssuer()
        => string.IsNullOrWhiteSpace(Issuer) ? DefaultIssuer : Issuer;

    internal string ResolveAudience()
        => string.IsNullOrWhiteSpace(Audience) ? DefaultAudience : Audience;

    internal int ResolveMaxLifetimeMinutes()
    {
        if (MaxLifetimeMinutes <= 0)
        {
            return DefaultMaxLifetimeMinutes;
        }

        return Math.Min(MaxLifetimeMinutes, MaxAllowedLifetimeMinutes);
    }

    internal byte[] ResolveKeyBytes()
    {
        var resolved = SecretResolver.Resolve(SigningKey) ?? string.Empty;
        return Encoding.UTF8.GetBytes(resolved);
    }
}
