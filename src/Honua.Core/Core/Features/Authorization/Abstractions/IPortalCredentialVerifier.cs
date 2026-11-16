// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IPortalCredentialVerifier
{
    Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken);
}

public sealed record PortalCredentialPrincipal(
    string PrincipalId,
    string? DisplayName,
    string? TenantId,
    IReadOnlyList<string> Roles,
    bool RolesRequireClaimsMappingEntitlement = false,
    bool TenantRequiresClaimsMappingEntitlement = false,
    IReadOnlyList<string>? RolesWithoutClaimsMapping = null,
    PortalCredentialSource? Source = null)
{
    // Roles flagged by RolesRequireClaimsMappingEntitlement that differ from the fallback only through the configured role claim types are revalidated against identity.oidc instead of identity.claims-mapping.
    public bool RolesDependOnlyOnRoleClaimSelection { get; init; }
}
