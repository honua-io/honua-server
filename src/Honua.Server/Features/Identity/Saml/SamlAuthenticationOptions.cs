// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Configuration for the SAML 2.0 Service Provider (SP) bridge (#508). Honua acts as an SP:
/// it publishes SP metadata, receives a signed SAML assertion at its Assertion Consumer
/// Service (ACS), validates the signature against the configured IdP certificate, maps
/// attributes/groups to RBAC roles, and establishes a Honua session.
/// </summary>
public sealed class SamlAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Saml";

    /// <summary>
    /// Whether the SAML bridge is enabled. When false the ACS and metadata endpoints reject
    /// requests so the surface cannot be exercised before an IdP is configured.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The SP entity ID (audience) this server identifies as. Assertions whose
    /// <c>AudienceRestriction</c> does not match are rejected.
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// The expected IdP issuer (<c>Issuer</c> element). Assertions from a different issuer are
    /// rejected.
    /// </summary>
    public string? IdpEntityId { get; set; }

    /// <summary>
    /// The Assertion Consumer Service URL (the SP endpoint the IdP POSTs the assertion to).
    /// Used in SP metadata and validated against the assertion's <c>Recipient</c> when present.
    /// </summary>
    public string? AssertionConsumerServiceUrl { get; set; }

    /// <summary>
    /// The IdP's signing certificate, base64-encoded DER (the inner text of an
    /// <c>&lt;X509Certificate&gt;</c> element). Required: signatures are verified against this
    /// certificate's public key, and unsigned assertions are rejected.
    /// </summary>
    public string? IdpSigningCertificate { get; set; }

    /// <summary>
    /// SAML attribute name whose values map to Honua role names (e.g. a group/role claim).
    /// Defaults to the common OID-less friendly name "Role".
    /// </summary>
    public string RoleAttribute { get; set; } = "Role";

    /// <summary>
    /// SAML attribute name carrying the user's email, if distinct from the NameID.
    /// </summary>
    public string EmailAttribute { get; set; } = "email";

    /// <summary>
    /// SAML attribute name carrying the user's display name.
    /// </summary>
    public string DisplayNameAttribute { get; set; } = "displayName";

    /// <summary>
    /// Default role assigned when the assertion carries no mapped role attribute. Null leaves
    /// the session role-less (authenticated but unprivileged).
    /// </summary>
    public string? DefaultRole { get; set; }

    /// <summary>
    /// Clock-skew tolerance (seconds) applied to <c>NotBefore</c>/<c>NotOnOrAfter</c> condition
    /// checks. Defaults to 120s.
    /// </summary>
    public int AllowedClockSkewSeconds { get; set; } = 120;

    /// <summary>
    /// Session lifetime (seconds) for the Honua session established from a SAML assertion when
    /// the assertion does not specify a shorter <c>SessionNotOnOrAfter</c>. Defaults to 3600s.
    /// </summary>
    public int SessionLifetimeSeconds { get; set; } = 3600;
}
