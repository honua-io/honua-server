// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Authorization requirement for the Studio package lifecycle surface (honua-server#3001).
/// Admits the admin family, matching the pre-#3001 posture, or any authenticated principal
/// once <see cref="StudioEndUserAuthorizationOptions.Enabled"/> is turned on. This policy only
/// answers "may the caller reach the Studio lifecycle endpoint group at all" -- the
/// fine-grained per-resource ownership check and the elevated-operation (publish-request,
/// rollback) operator-grant check happen inside each endpoint handler via
/// <see cref="Honua.Core.Features.Studio.Abstractions.IStudioAuthorizationService"/>, because
/// they need the specific draft/content-item's recorded owner, which is not available at the
/// ASP.NET route-group policy-evaluation point.
/// </summary>
internal sealed class StudioLifecycleRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="StudioLifecycleRequirement"/> against the caller's admin role and the
/// <c>Studio:EndUserAuthorization:Enabled</c> flag.
/// </summary>
internal sealed class StudioLifecycleAuthorizationHandler(
    IOptionsMonitor<StudioEndUserAuthorizationOptions> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<StudioLifecycleAuthorizationHandler> logger)
    : AuthorizationHandler<StudioLifecycleRequirement>
{
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options = options;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<StudioLifecycleAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioLifecycleRequirement requirement)
    {
        if (context.User.IsInRole("admin"))
        {
            // Preserve the scoped admin-permission boundary this policy replaced (#1985): the
            // prior RequireAdminAuthorization() gate for this group also ran
            // AdminPermissionRequirement, so an admin:read-scoped key could read but never
            // mutate Studio resources. Widening this policy to admit non-admin end users below
            // must not silently drop that check for admin-tier callers -- enforce the identical
            // method-scoped grant here, unconditionally and independent of the end-user flag
            // (admin behavior must stay byte-for-byte unchanged; see AdminPermissionAuthorizationHandler,
            // whose requirement this policy previously ran via RequireAdminAuthorization()).
            var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
            var method = httpContext?.Request.Method;
            if (AdminApiKeyPermission.IsAuthorized(context.User, method))
            {
                context.Succeed(requirement);
            }
            else
            {
                AuthenticationLog.ScopedAdminKeyDenied(_logger, method ?? "(unknown)");
            }

            return Task.CompletedTask;
        }

        if (_options.CurrentValue.Enabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        AuthenticationLog.StudioEndUserModeDenied(_logger);
        // Leave the requirement unmet (do not Fail): yields a 403 and lets other
        // handlers/requirements report their own outcome, matching the admin handler.
        return Task.CompletedTask;
    }
}
