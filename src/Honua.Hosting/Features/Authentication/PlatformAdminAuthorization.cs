// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.MultiTenancy;

namespace Honua.Infrastructure.Authentication;

internal static class PlatformAdminAuthorization
{
    public static bool IsAuthorized(ClaimsPrincipal principal, TenantContextOptions? options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        options ??= new TenantContextOptions();
        if (!options.Enabled)
        {
            return true;
        }

        if (principal.Identity?.IsAuthenticated == true &&
            options.MultiTenantAdminRoles.Any(role => !string.IsNullOrWhiteSpace(role) && principal.IsInRole(role)))
        {
            return true;
        }

        return !IsTenantBound(principal, options);
    }

    // The recovery grant's tenant comes from the validated identity and never from a request header.
    public static string? ResolveTenantId(ClaimsPrincipal? principal, TenantContextOptions? options)
    {
        options ??= new TenantContextOptions();
        if (principal == null || !options.Enabled)
        {
            return null;
        }

        return TenantClaimTypes(options)
            .SelectMany(principal.FindAll)
            .Select(claim => claim.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    // A tenant binding comes from the validated identity (the configured tenant claims) or
    // from a server-issued approved-operation credential; never from a request header.
    private static bool IsTenantBound(ClaimsPrincipal principal, TenantContextOptions options) =>
        TenantClaimTypes(options).Any(claimType => principal.FindAll(claimType).Any(claim => !string.IsNullOrWhiteSpace(claim.Value)));

    private static IEnumerable<string> TenantClaimTypes(TenantContextOptions options) =>
        options.TenantClaimTypes
            .Where(claimType => !string.IsNullOrWhiteSpace(claimType))
            .Append(AdminApiKeyPermission.ApprovedOperationTenantClaim);
}
