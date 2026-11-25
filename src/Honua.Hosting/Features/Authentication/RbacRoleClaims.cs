// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;

namespace Honua.Infrastructure.Authentication;

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
            LicenseGate.HasLiveEntitlement(serviceProvider, FeatureCatalog.OidcAuthenticationKey));

    public static List<string> Enumerate(
        ClaimsPrincipal principal,
        RbacOptions options,
        bool roleClaimSelectionEntitled)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        // A non-default Rbac:RoleClaimType is role claim selection, just like OIDC's
        // provider-specific role claim configuration, and needs identity.oidc. Consult the live snapshot on
        // every authorization decision so applying or expiring a license takes
        // effect without a restart. Normalized and default role claims remain
        // available in every edition.
        var customClaimType = roleClaimSelectionEntitled ? ResolveCustomClaimType(options) : null;
        var roles = new List<string>();
        foreach (var claim in principal.Claims)
        {
            if (!IsAcceptedRoleClaimType(claim.Type, customClaimType) || string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            var alreadyPresent = false;
            for (var i = 0; i < roles.Count; i++)
            {
                if (string.Equals(roles[i], claim.Value, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
            {
                roles.Add(claim.Value);
            }
        }

        return roles;
    }

    public static bool IsAdmin(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider? serviceProvider)
        => IsAdmin(
            principal,
            options,
            serviceProvider is not null &&
            LicenseGate.HasLiveEntitlement(serviceProvider, FeatureCatalog.OidcAuthenticationKey));

    public static bool IsAdmin(
        ClaimsPrincipal principal,
        RbacOptions options,
        bool roleClaimSelectionEntitled)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var customClaimType = roleClaimSelectionEntitled ? ResolveCustomClaimType(options) : null;
        foreach (var claim in principal.Claims)
        {
            if (IsAcceptedRoleClaimType(claim.Type, customClaimType)
                && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveCustomClaimType(RbacOptions options)
    {
        var configuredClaimType = options.EffectiveRoleClaimType;
        if (string.Equals(configuredClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredClaimType, RbacOptions.DefaultRoleClaimType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return configuredClaimType;
    }

    private static bool IsAcceptedRoleClaimType(string claimType, string? customClaimType)
        => string.Equals(claimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
           || string.Equals(claimType, RbacOptions.DefaultRoleClaimType, StringComparison.OrdinalIgnoreCase)
           || (customClaimType is not null && string.Equals(claimType, customClaimType, StringComparison.OrdinalIgnoreCase));
}
