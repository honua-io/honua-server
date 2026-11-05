// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

// While tenant resolution is enabled, a tenant-bound principal also needs a platform administrator role to create or advance a deploy.
internal static class PlatformDeployAuthority
{
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
