// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

// ops:read satisfies a safe read and is denied every mutating ops endpoint, which keep an admin policy this grant cannot satisfy.
internal sealed class OpsReadRequirement : IAuthorizationRequirement;

internal sealed class OpsReadAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<OpsReadAuthorizationHandler> logger)
    : AuthorizationHandler<OpsReadRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<OpsReadAuthorizationHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OpsReadRequirement requirement)
    {
        // Mirror AdminPermissionAuthorizationHandler: the HTTP method is the behavior-changing input;
        // prefer the resource the middleware supplies and fall back to the ambient accessor.
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        var method = httpContext?.Request.Method;

        if (AdminApiKeyPermission.IsOpsReadAuthorized(context.User, method))
        {
            context.Succeed(requirement);
        }
        else
        {
            AuthenticationLog.OpsReaderKeyDenied(_logger, method ?? "(unknown)");
            // Leave the requirement unmet (do not Fail): yields a 403 and lets other
            // handlers/requirements report their own outcome, matching the admin handler.
        }

        return Task.CompletedTask;
    }
}
