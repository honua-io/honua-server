// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Selects and configures the <c>IPortalCredentialVerifier</c> used by the
/// ArcGIS-compatible <c>/sharing/rest/generateToken</c> endpoint (#1370).
/// </summary>
/// <remarks>
/// The default verifier remains <c>AdminPortalCredentialVerifier</c> (admin
/// password / admin API key) — the community/fallback option. Operators that run
/// an external IdP can opt into the OIDC-backed verifier, which authenticates a
/// named-user login against the shared OIDC core (#348) instead of a Portal-local
/// user store. Both projections feed the same <c>IPortalTokenIssuer</c>, so the
/// hydrated principal yields the same RBAC decision as a direct OIDC session.
/// </remarks>
public sealed class PortalCredentialVerifierOptions
{
    /// <summary>
    /// Configuration section binding root.
    /// </summary>
    public const string SectionName = "Authentication:PortalCredentialVerifier";

    /// <summary>
    /// Whether the OIDC-backed verifier is enabled. Defaults to
    /// <see langword="false"/> so the admin verifier stays the default and named-
    /// user OIDC login is strictly opt-in (#1370 acceptance criteria).
    /// </summary>
    public bool UseOidc { get; set; }

    /// <summary>
    /// When the OIDC verifier is enabled, whether to fall back to the admin
    /// verifier if the supplied credential is not a valid OIDC token. Defaults to
    /// <see langword="true"/> so an operator can keep admin/API-key access working
    /// alongside named-user login during a migration.
    /// </summary>
    public bool FallBackToAdminVerifier { get; set; } = true;
}
