// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;

namespace Honua.ControlPlane;

/// <summary>
/// Captures and restores the principal that created a coordinated release so the container
/// step can run the child deploy under that identity instead of <c>principal: null</c>.
/// </summary>
internal static class CoordinatedReleasePrincipal
{
    /// <summary>
    /// Copies subject, name, roles, authentication type, and tenant binding onto the context.
    /// An unauthenticated or missing principal leaves the snapshot empty.
    /// </summary>
    public static CoordinatedReleaseContext Capture(
        CoordinatedReleaseContext context,
        ClaimsPrincipal? principal,
        TenantContextOptions? tenantOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (principal?.Identity is not { } identity)
        {
            return context;
        }

        var options = tenantOptions ?? new TenantContextOptions();
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;
        var roles = principal.Claims
            .Where(claim =>
                claim.Type == ClaimTypes.Role
                || string.Equals(claim.Type, "role", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Type, RbacOptions.DefaultRoleClaimType, StringComparison.OrdinalIgnoreCase))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? tenantId = null;
        string? tenantClaimType = null;
        if (options.Enabled)
        {
            foreach (var claimType in TenantClaimTypes(options))
            {
                var value = principal.FindAll(claimType)
                    .Select(claim => claim.Value)
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
                if (value is null)
                {
                    continue;
                }

                tenantId = value;
                tenantClaimType = claimType;
                break;
            }
        }

        return context with
        {
            RequesterSubject = string.IsNullOrWhiteSpace(subject) ? null : subject,
            RequesterName = string.IsNullOrWhiteSpace(identity.Name) ? null : identity.Name,
            RequesterAuthenticationType = string.IsNullOrWhiteSpace(identity.AuthenticationType)
                ? null
                : identity.AuthenticationType,
            RequesterRoles = roles,
            RequesterTenantId = tenantId,
            RequesterTenantClaimType = tenantClaimType,
        };
    }

    /// <summary>
    /// Rebuilds a principal from the snapshot. An empty snapshot is unauthenticated and has no
    /// roles, so it cannot satisfy an admin exemption or invent a tenant binding.
    /// </summary>
    public static ClaimsPrincipal Restore(CoordinatedReleaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var claims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(context.RequesterSubject))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, context.RequesterSubject));
            claims.Add(new Claim("sub", context.RequesterSubject));
        }

        if (!string.IsNullOrWhiteSpace(context.RequesterName))
        {
            claims.Add(new Claim(ClaimTypes.Name, context.RequesterName));
        }

        foreach (var role in context.RequesterRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
            claims.Add(new Claim(RbacOptions.DefaultRoleClaimType, role));
        }

        if (!string.IsNullOrWhiteSpace(context.RequesterTenantId))
        {
            var claimType = string.IsNullOrWhiteSpace(context.RequesterTenantClaimType)
                ? "tenant_id"
                : context.RequesterTenantClaimType;
            AddTenant(claims, claimType, context.RequesterTenantId);
            AddTenant(claims, "tenant_id", context.RequesterTenantId);
            AddTenant(claims, "tid", context.RequesterTenantId);
            AddTenant(claims, AdminApiKeyPermission.ApprovedOperationTenantClaim, context.RequesterTenantId);
        }

        // Only a snapshot that actually carries identity is authenticated. Authentication type
        // alone must not turn an empty legacy record into an authenticated admin.
        var authenticated = claims.Count > 0;
        var authenticationType = authenticated
            ? context.RequesterAuthenticationType ?? "CoordinatedRelease"
            : null;
        var restored = new ClaimsIdentity(
            claims,
            authenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(restored);
    }

    private static void AddTenant(List<Claim> claims, string claimType, string tenantId)
    {
        if (claims.Any(claim => claim.Type == claimType && claim.Value == tenantId))
        {
            return;
        }

        claims.Add(new Claim(claimType, tenantId));
    }

    private static IEnumerable<string> TenantClaimTypes(TenantContextOptions options) =>
        options.TenantClaimTypes
            .Where(claimType => !string.IsNullOrWhiteSpace(claimType))
            .Append(AdminApiKeyPermission.ApprovedOperationTenantClaim);
}
