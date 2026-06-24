// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Authorization requirement that enforces a scoped admin API key's persisted
/// permission grants on the general request path (#1985).
/// </summary>
/// <remarks>
/// The <c>Admin</c> family of policies previously required only the <c>admin</c>
/// role, which every authenticated admin key received regardless of its scope.
/// This requirement is added alongside that role check so a key minted as
/// read-only (<c>admin:read</c>) can satisfy safe (GET/HEAD/OPTIONS) admin reads
/// but is denied mutating (POST/PUT/PATCH/DELETE) admin operations. Full-admin
/// principals (<c>admin:*</c>, the bootstrap password, client certificates, and
/// the Test dev-bypass) are unaffected.
/// </remarks>
internal sealed class AdminPermissionRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="AdminPermissionRequirement"/> against the principal's
/// admin permission grants and the current request's HTTP method.
/// </summary>
internal sealed class AdminPermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<AdminPermissionAuthorizationHandler> logger)
    : AuthorizationHandler<AdminPermissionRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<AdminPermissionAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
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

        if (AdminApiKeyPermission.IsAuthorized(context.User, method))
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
