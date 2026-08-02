// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Resolves the normalized role claims shared by every RBAC authorization surface.
/// </summary>
internal static class RbacRoleClaims
{
    public static List<string> Enumerate(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider? serviceProvider)
        => Enumerate(
            principal,
            options,
            serviceProvider is not null &&
            LicenseGate.HasLiveEntitlement(serviceProvider, FeatureCatalog.OidcClaimsMappingKey));

    public static List<string> Enumerate(
        ClaimsPrincipal principal,
        RbacOptions options,
        bool claimsMappingEntitled)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var acceptedClaimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ClaimTypes.Role,
            RbacOptions.DefaultRoleClaimType,
        };

        var configuredClaimType = options.EffectiveRoleClaimType;
        var isCustomClaimType = !string.Equals(
                configuredClaimType,
                ClaimTypes.Role,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                configuredClaimType,
                RbacOptions.DefaultRoleClaimType,
                StringComparison.OrdinalIgnoreCase);

        // A non-default Rbac:RoleClaimType is claims mapping, just like OIDC's
        // provider-specific role claim configuration. Consult the live snapshot on
        // every authorization decision so applying or expiring a license takes
        // effect without a restart. Normalized and default role claims remain
        // available in every edition.
        if (isCustomClaimType && claimsMappingEntitled)
        {
            acceptedClaimTypes.Add(configuredClaimType);
        }

        return principal.Claims
            .Where(claim =>
                acceptedClaimTypes.Contains(claim.Type) &&
                !string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsAdmin(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider? serviceProvider)
        => Enumerate(principal, options, serviceProvider)
            .Any(role => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));

    public static bool IsAdmin(
        ClaimsPrincipal principal,
        RbacOptions options,
        bool claimsMappingEntitled)
        => Enumerate(principal, options, claimsMappingEntitled)
            .Any(role => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));
}
