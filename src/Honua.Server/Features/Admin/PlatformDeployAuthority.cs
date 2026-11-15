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

    public static bool IsAuthorized(ClaimsPrincipal principal, TenantContextOptions? options) =>
        PlatformAdminAuthorization.IsAuthorized(principal, options);

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

    public static string? ResolveTenantId(ClaimsPrincipal? principal, TenantContextOptions? options) =>
        PlatformAdminAuthorization.ResolveTenantId(principal, options);
}
