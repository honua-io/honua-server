// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

// admin:read may use safe methods only; a full-admin principal is unaffected.
internal sealed class AdminPermissionRequirement : IAuthorizationRequirement;

internal sealed class AdminPermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<AdminPermissionAuthorizationHandler> logger)
    : AuthorizationHandler<AdminPermissionRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<AdminPermissionAuthorizationHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminPermissionRequirement requirement)
    {
        // The HTTP method is the behavior-changing input. Prefer the resource the
        // authorization middleware supplies (HttpContext in minimal APIs) and fall
        // back to the ambient accessor so the requirement also works when invoked
        // outside the endpoint resource (e.g. imperative IAuthorizationService).
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        var method = httpContext?.Request.Method;

        if (AdminApiKeyPermission.IsAuthorized(context.User, method, httpContext?.Request.Path.Value))
        {
            context.Succeed(requirement);
        }
        else
        {
            AuthenticationLog.ScopedAdminKeyDenied(_logger, method ?? "(unknown)");
            // Do not call context.Fail(): leaving the requirement unmet yields a 403
            // and lets other handlers/requirements report their own outcome.
        }

        return Task.CompletedTask;
    }
}
