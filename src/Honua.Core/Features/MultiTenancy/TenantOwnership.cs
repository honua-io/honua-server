// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.MultiTenancy;

public static class TenantOwnership
{
    public static string? Normalize(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();

    // A record with no tenant belongs to the default tenant, so a tenant-tagged principal does not inherit unstamped content.
    public static string? ResolveOwnerTenant(string? recordTenantId, TenantIsolationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Normalize(recordTenantId) ?? Normalize(options.DefaultTenantId);
    }

    public static bool CanAccess(
        string? recordTenantId,
        string? requestTenantId,
        TenantIsolationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Tenant resolution disabled: there is no tenant rail to enforce, and every record is
        // reachable exactly as it was before tenant stamping existed.
        if (!options.Enabled)
        {
            return true;
        }

        return string.Equals(
            ResolveOwnerTenant(recordTenantId, options),
            Normalize(requestTenantId),
            StringComparison.Ordinal);
    }

    public static bool IsMultiTenantAdmin(ClaimsPrincipal? principal, TenantIsolationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        var roles = options.MultiTenantAdminRoles;
        return roles is not null
            && roles.Any(role => !string.IsNullOrWhiteSpace(role) && principal.IsInRole(role));
    }

    public static TenantScopeFilter? CreateScopeFilter(
        ClaimsPrincipal? principal,
        string? requestTenantId,
        TenantIsolationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || IsMultiTenantAdmin(principal, options))
        {
            return null;
        }

        var tenantId = Normalize(requestTenantId);
        return new TenantScopeFilter(
            tenantId,
            // Records with no recorded tenant belong to the default tenant, so they are only
            // enumerable by a request that resolved to that same default.
            string.Equals(tenantId, Normalize(options.DefaultTenantId), StringComparison.Ordinal));
    }
}

public sealed record TenantScopeFilter(string? TenantId, bool IncludeUnassigned);
