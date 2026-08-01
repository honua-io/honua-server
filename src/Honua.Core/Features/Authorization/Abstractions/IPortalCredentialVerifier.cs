// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Verifies username/password credentials supplied to the ArcGIS-compatible
/// <c>/sharing/rest/generateToken</c> endpoint and projects the verified
/// principal onto the inputs required to mint a portal token.
/// </summary>
/// <remarks>
/// The default implementation bridges to the same admin-password / admin
/// API-key surface that the API-key handler consumes. Operators that integrate
/// an external identity store (LDAP, OIDC introspection, custom) can register
/// their own implementation to override the bridge.
/// </remarks>
public interface IPortalCredentialVerifier
{
    /// <summary>
    /// Verifies the supplied username/password pair.
    /// </summary>
    /// <param name="username">User-supplied username.</param>
    /// <param name="password">User-supplied password.</param>
    /// <param name="cancellationToken">Token used to abort verification.</param>
    /// <returns>The verified principal context, or <see langword="null"/> when verification fails.</returns>
    Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>
/// Verified principal projected from a credential check.
/// </summary>
/// <param name="PrincipalId">Stable identifier for the authenticated principal.</param>
/// <param name="DisplayName">Display name surfaced through the issued token's hydrated principal.</param>
/// <param name="TenantId">Tenant scope to attach to the issued token, or <see langword="null"/>.</param>
/// <param name="Roles">Roles granted for the lifetime of the issued token.</param>
/// <param name="RolesRequireClaimsMappingEntitlement">
/// True when <paramref name="Roles"/> were produced with the Enterprise
/// <c>identity.claims-mapping</c> entitlement active and would differ without it. The issued
/// token persists these roles, and the restore path never re-runs the claims transformation, so
/// this flag is what lets the restore revalidate them against the LIVE entitlement instead of
/// honouring a snapshot taken while the license was valid (honua-server#2997 review).
/// </param>
public sealed record PortalCredentialPrincipal(
    string PrincipalId,
    string? DisplayName,
    string? TenantId,
    IReadOnlyList<string> Roles,
    bool? RolesRequireClaimsMappingEntitlement = null);
