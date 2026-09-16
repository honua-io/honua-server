// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Authority boundary for creating or advancing Deploy and platform-release operations
/// (honua-server#4842). Deployment targets are platform resources with no owning tenant,
/// so while tenant resolution is enabled a tenant-bound principal must also hold one of
/// the configured multi-tenant (platform) administrator roles. Principals without a tenant
/// binding, and installations with <c>MultiTenancy:Enabled=false</c>, keep the existing
/// admin-policy and operator-scope authorization unchanged.
/// </summary>
internal static class PlatformDeployAuthority
{
    /// <summary>Stable machine-readable denial code surfaced by REST and MCP.</summary>
    internal const string DenialCode = "platform_admin_required";

    internal const string DenialMessage =
        "Deploy and platform-release operations require a platform administrator when multi-tenancy is enabled.";

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

    /// <summary>Returns a 403 problem when the caller lacks platform deploy authority.</summary>
    public static IResult? Deny(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value;
        return IsAuthorized(context.User, options)
            ? null
            : Results.Problem(
                title: "Platform administrator required",
                detail: DenialMessage,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = DenialCode });
    }

    /// <summary>
    /// Whether the principal holds one of the configured platform (multi-tenant) administrator roles
    /// (honua-server#4958). This is the explicitly broader role that may act outside a recovery grant's
    /// recorded actor/tenant binding.
    /// </summary>
    public static bool IsPlatformAdministrator(ClaimsPrincipal principal, TenantContextOptions? options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        options ??= new TenantContextOptions();
        return principal.Identity?.IsAuthenticated == true &&
            options.MultiTenantAdminRoles.Any(role => !string.IsNullOrWhiteSpace(role) && principal.IsInRole(role));
    }

    /// <summary>
    /// The principal's tenant binding, or null when it has none (honua-server#4958). Read from the same
    /// validated sources as <see cref="IsTenantBound"/> so a recovery grant's tenant can never be
    /// asserted by a request header. Returns null when tenant resolution is disabled, which keeps a
    /// single-tenant installation's recovery fence purely actor-bound.
    /// </summary>
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
