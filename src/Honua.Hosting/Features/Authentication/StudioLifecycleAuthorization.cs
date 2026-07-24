// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
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
    IOptionsMonitor<AdminRoleOptions> adminRoleOptions,
    IHttpContextAccessor httpContextAccessor,
    ILogger<StudioLifecycleAuthorizationHandler> logger)
    : AuthorizationHandler<StudioLifecycleRequirement>
{
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options = options;
    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions = adminRoleOptions;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<StudioLifecycleAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioLifecycleRequirement requirement)
    {
        if (IsRecognizedAdmin(context.User))
        {
            // Preserve the scoped admin-permission boundary this policy replaced (#1985) --
            // but only for API-key principals. The prior RequireAdminAuthorization() gate ran
            // AdminPermissionRequirement so an admin:read-scoped key could read but never
            // mutate Studio resources; that grammar classifies the "permission" claims the
            // ApiKey scheme stamps. OIDC/session/cert admin principals never went through it
            // under OIDC deployments (UpdateRolePolicy rebuilds the admin policies as
            // role-assertion-only), and an IdP may attach unrelated "permission" claims that
            // the grammar would misclassify as a scoped key and deny -- so the grammar applies
            // exactly to identities authenticated by the ApiKey scheme and no others.
            if (!IsApiKeyPrincipal(context.User))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

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

    /// <summary>
    /// Recognizes the admin family the pre-#3001 policies admitted: the literal <c>admin</c>
    /// role plus the configured <c>Oidc:AdminRoles</c> aliases (see <see cref="AdminRoleOptions"/>,
    /// the same set <c>StudioAuthorizationService.IsAdmin</c> resolves), so an OIDC admin via
    /// an alias role keeps admin-tier Studio access rather than degrading to end-user scoping.
    /// </summary>
    private bool IsRecognizedAdmin(ClaimsPrincipal principal)
    {
        if (principal.IsInRole("admin"))
        {
            return true;
        }

        var aliases = _adminRoleOptions.CurrentValue.AdminRoles;
        if (aliases is null)
        {
            return false;
        }

        return aliases.Any(alias => !string.IsNullOrWhiteSpace(alias) && principal.IsInRole(alias));
    }

    private static bool IsApiKeyPrincipal(ClaimsPrincipal principal)
        => principal.Identities.Any(identity =>
            string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal));
}
