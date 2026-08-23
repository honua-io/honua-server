// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
/// Additional boundary for the Studio AI proxy. The general Studio lifecycle policy may admit
/// scoped API keys so they can own drafts, but model-provider access is reserved for interactive
/// OIDC/session users and administrators until a dedicated Studio AI permission exists.
/// </summary>
internal sealed class StudioAiProxyRequirement : IAuthorizationRequirement;

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
    internal const string ScopedAdminPermissionDeniedCode =
        "studio_authorization/admin_permission_denied";

    internal const string PolicyDeniedCode = "studio_authorization/policy_denied";

    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options = options;
    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions = adminRoleOptions;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<StudioLifecycleAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioLifecycleRequirement requirement)
    {
        if (IsRecognizedAdmin(context.User, _adminRoleOptions.CurrentValue))
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
                context.Fail(new AuthorizationFailureReason(this, ScopedAdminPermissionDeniedCode));
            }

            return Task.CompletedTask;
        }

        if (_options.CurrentValue.Enabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        AuthenticationLog.StudioEndUserModeDenied(_logger);
        // Carry the stable Studio denial code into the middleware result handler. Leaving the
        // requirement merely unmet delegates to the authentication scheme's generic 403 body,
        // which violates the Studio lifecycle RFC 7807 contract.
        context.Fail(new AuthorizationFailureReason(this, StudioAuthorizationService.EndUserModeDisabledCode));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recognizes the admin family the pre-#3001 policies admitted: the literal <c>admin</c>
    /// role plus the configured <c>Oidc:AdminRoles</c> aliases (see <see cref="AdminRoleOptions"/>,
    /// the same set <c>StudioAuthorizationService.IsAdmin</c> resolves), so an OIDC admin via
    /// an alias role keeps admin-tier Studio access rather than degrading to end-user scoping.
    /// </summary>
    internal static bool IsRecognizedAdmin(ClaimsPrincipal principal, AdminRoleOptions options)
    {
        if (principal.IsInRole("admin"))
        {
            return true;
        }

        var aliases = options.AdminRoles;
        if (aliases is null)
        {
            return false;
        }

        return aliases.Any(alias => !string.IsNullOrWhiteSpace(alias) && principal.IsInRole(alias));
    }

    internal static bool IsApiKeyPrincipal(ClaimsPrincipal principal)
        => principal.Identities.Any(identity =>
            string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal));

    internal static bool IsInteractivePrincipal(ClaimsPrincipal principal)
        => StudioAiInteractivePrincipal.IsInteractive(principal);
}

/// <summary>
/// Keeps non-admin API keys out of the Studio AI proxy while preserving both interactive
/// end-user access and the existing admin family.
/// </summary>
internal sealed class StudioAiProxyAuthorizationHandler(
    IOptionsMonitor<AdminRoleOptions> adminRoleOptions)
    : AuthorizationHandler<StudioAiProxyRequirement>
{
    internal const string InteractivePrincipalRequiredCode =
        "studio_authorization/interactive_principal_required";

    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions = adminRoleOptions;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioAiProxyRequirement requirement)
    {
        if (StudioLifecycleAuthorizationHandler.IsRecognizedAdmin(
                context.User,
                _adminRoleOptions.CurrentValue) ||
            StudioLifecycleAuthorizationHandler.IsInteractivePrincipal(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        context.Fail(new AuthorizationFailureReason(this, InteractivePrincipalRequiredCode));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the AI proxy's interactive admission list explicit. Non-admin mTLS and other machine
/// identities must not be treated as browser/session users merely because they are not API keys.
/// </summary>
internal static class StudioAiInteractivePrincipal
{
    private static readonly string[] InteractiveAuthenticationSchemes =
    [
        JwtBearerDefaults.AuthenticationScheme,
        OidcAuthenticationExtensions.CompositeScheme,
        OidcAuthenticationExtensions.AdminSessionScheme,
        OidcAuthenticationExtensions.OidcScheme,
        OidcAuthenticationExtensions.GoogleScheme,
        OidcAuthenticationExtensions.OktaScheme,
        OidcAuthenticationExtensions.Auth0Scheme,
        OidcAuthenticationExtensions.AzureAdScheme,
    ];

    internal static bool IsInteractive(ClaimsPrincipal principal)
    {
        var hasMachineGrant = principal.Identities.Any(identity => identity.HasClaim(c =>
            (string.Equals(c.Type, "grant_type", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(c.Type, "client_credentials", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(c.Type, "honua_auth_flow", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase))));
        return !hasMachineGrant && principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            ((InteractiveAuthenticationSchemes.Any(scheme =>
                  string.Equals(identity.AuthenticationType, scheme, StringComparison.OrdinalIgnoreCase)) &&
              IsHumanSession(identity))));
    }

    private static bool IsHumanSession(ClaimsIdentity identity)
    {
        // A validated bearer token is not necessarily interactive: client-credentials tokens
        // can carry the same scope-governed marker as user tokens. Require a positive user
        // identity signal for bearer auth and reject explicit machine-grant markers.
        if (string.Equals(identity.AuthenticationType, JwtBearerDefaults.AuthenticationScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (identity.HasClaim(c =>
                    (string.Equals(c.Type, "grant_type", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(c.Type, "client_credentials", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(c.Type, "honua_auth_flow", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            return identity.HasClaim(c =>
                string.Equals(c.Type, "honua_interactive_provenance", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}


/// <summary>
/// Audits Studio lifecycle route-group policy denials that short-circuit before the endpoint
/// middleware can observe them, and preserves the Studio RFC 7807 response for the flag-off
/// denial while delegating every other response to ASP.NET's default result handler.
/// </summary>
internal sealed class StudioLifecycleAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private const string StudioProblemType = "https://honua.io/problems/studio";
    private const string EndUserModeDisabledDetail = "Studio package lifecycle operations require the admin role.";
    private const string InteractivePrincipalRequiredDetail =
        "Studio AI proxy operations require an interactive user session or the admin role.";
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var denialCode = ResolvePolicyDenialCode(policy, authorizeResult);
        if (denialCode is not null)
        {
            await RecordPolicyDenialAuditAsync(context, denialCode).ConfigureAwait(false);
        }

        var denialDetail = denialCode switch
        {
            StudioAuthorizationService.EndUserModeDisabledCode => EndUserModeDisabledDetail,
            StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode => InteractivePrincipalRequiredDetail,
            _ => null,
        };
        if (authorizeResult.Forbidden && denialDetail is not null)
        {
            await ProblemDetailsHelpers.CreateProblem(
                context,
                StudioProblemType,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                denialDetail,
                denialCode!).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    private static string? ResolvePolicyDenialCode(
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!policy.Requirements.Any(static requirement => requirement is StudioLifecycleRequirement))
        {
            return null;
        }

        // Authentication challenges are policy denials too, but retain the existing 401
        // challenge response. Give them the same stable code the endpoint authorization
        // service uses when no authenticated caller can be resolved.
        if (authorizeResult.Challenged)
        {
            return StudioAuthorizationService.AuthenticationRequiredCode;
        }

        if (!authorizeResult.Forbidden)
        {
            return null;
        }

        if (HasFailureReason(authorizeResult, StudioAuthorizationService.EndUserModeDisabledCode))
        {
            return StudioAuthorizationService.EndUserModeDisabledCode;
        }

        if (HasFailureReason(authorizeResult, StudioLifecycleAuthorizationHandler.ScopedAdminPermissionDeniedCode))
        {
            return StudioLifecycleAuthorizationHandler.ScopedAdminPermissionDeniedCode;
        }

        if (HasFailureReason(authorizeResult, StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode))
        {
            return StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode;
        }

        // Future requirements added to the Studio policy must not silently create a new
        // unaudited short-circuit. Preserve their normal response while recording one stable
        // generic policy-denial code until a more specific reason is introduced.
        return StudioLifecycleAuthorizationHandler.PolicyDeniedCode;
    }

    private static bool HasFailureReason(
        PolicyAuthorizationResult authorizeResult,
        string code)
        => authorizeResult.AuthorizationFailure?.FailureReasons.Any(reason =>
            string.Equals(reason.Message, code, StringComparison.Ordinal)) == true;

    private static Task RecordPolicyDenialAuditAsync(HttpContext context, string code)
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return Task.CompletedTask;
        }

        var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var auditEvent = new AuditEvent
        {
            Timestamp = timeProvider.GetUtcNow(),
            EventType = AuditEventType.Authorization,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = "studio",
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = "studio.lifecycle",
            Outcome = AuditOutcome.Denied,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = $"{{\"code\":\"{code}\"}}",
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }
}
