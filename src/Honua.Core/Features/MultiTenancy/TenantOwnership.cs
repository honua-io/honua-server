// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.MultiTenancy;

/// <summary>
/// Shared ownership boundary for durable records that record the tenant they were created in
/// (honua-server#4905). Mirrors <c>Honua.Infrastructure.MultiTenancy.OperationTenantAuthorization</c>
/// for feature slices that live in <c>Honua.Core</c> and therefore cannot see
/// ASP.NET Core's <c>HttpContext</c>.
/// </summary>
public static class TenantOwnership
{
    /// <summary>Trims a tenant id and collapses blank values to <see langword="null"/>.</summary>
    public static string? Normalize(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();

    /// <summary>
    /// Resolves the tenant a record belongs to. A record with no recorded tenant predates
    /// tenant stamping (or was written by a background worker with no request tenant) and
    /// belongs to the deployment's default tenant -- the tenant every request in a
    /// single-tenant deployment resolves to, so an upgrade keeps reading its own content while
    /// a tenant-tagged principal never inherits it.
    /// </summary>
    public static string? ResolveOwnerTenant(string? recordTenantId, TenantIsolationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Normalize(recordTenantId) ?? Normalize(options.DefaultTenantId);
    }

    /// <summary>
    /// Returns <see langword="true"/> when a request resolved to <paramref name="requestTenantId"/>
    /// may act on a record whose recorded tenant is <paramref name="recordTenantId"/>.
    /// </summary>
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

    /// <summary>
    /// Returns <see langword="true"/> when the principal holds a configured multi-tenant admin
    /// role and therefore operates across tenants.
    /// </summary>
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

    /// <summary>
    /// Builds the enumeration filter that scopes a listing to the request's tenant, or
    /// <see langword="null"/> when no tenant scoping applies (tenant resolution disabled, or a
    /// multi-tenant admin that legitimately enumerates every tenant).
    /// </summary>
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

/// <summary>
/// Tenant scoping applied to a durable listing: rows whose recorded tenant equals
/// <paramref name="TenantId"/>, plus -- when <paramref name="IncludeUnassigned"/> is
/// <see langword="true"/> -- rows that recorded no tenant at all.
/// </summary>
/// <param name="TenantId">The request's resolved tenant, or <see langword="null"/>.</param>
/// <param name="IncludeUnassigned">
/// Whether rows with no recorded tenant belong to <paramref name="TenantId"/>; see
/// <see cref="TenantOwnership.ResolveOwnerTenant"/>.
/// </param>
public sealed record TenantScopeFilter(string? TenantId, bool IncludeUnassigned);
