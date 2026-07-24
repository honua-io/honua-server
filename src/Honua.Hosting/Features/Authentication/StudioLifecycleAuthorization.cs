// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio;
using Microsoft.AspNetCore.Authorization;
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
    ILogger<StudioLifecycleAuthorizationHandler> logger)
    : AuthorizationHandler<StudioLifecycleRequirement>
{
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options = options;
    private readonly ILogger<StudioLifecycleAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioLifecycleRequirement requirement)
    {
        if (context.User.IsInRole("admin"))
        {
            context.Succeed(requirement);
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
