// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// This policy only admits the caller to the route group; ownership and elevated operations are enforced in the handler, where the recorded owner is known.
internal sealed class StudioLifecycleRequirement : IAuthorizationRequirement;

internal sealed class StudioAiProxyRequirement : IAuthorizationRequirement;

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

    internal static bool IsRecognizedAdmin(ClaimsPrincipal principal, AdminRoleOptions options)
    {
        if (principal.IsInRole("admin") || principal.IsInRole(AdminApiKeyPermission.ScopedAdminRole))
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

internal sealed class StudioAiProxyAuthorizationHandler(
    IOptionsMonitor<AdminRoleOptions> adminRoleOptions)
    : AuthorizationHandler<StudioAiProxyRequirement>
{
    internal const string InteractivePrincipalRequiredCode =
        "studio_authorization/interactive_principal_required";

    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions = adminRoleOptions;

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

// A non-admin machine identity is not treated as an interactive user merely because it is not an API key.
internal static class StudioAiInteractivePrincipal
{
    internal const string InteractiveProvenanceClaimType = "honua_interactive_provenance";
    private const string InteractiveProvenanceClaimValue = "true";

    private static readonly HashSet<string> HumanAuthenticationMethods = new(
        ["face", "fido", "fido2", "fingerprint", "iris", "mfa", "otp", "smartcard", "webauthn", "wia"],
        StringComparer.OrdinalIgnoreCase);

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
        return !HasMachineGrant(principal) && principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            (IsHumanSession(identity) &&
             (InteractiveAuthenticationSchemes.Any(scheme =>
                  string.Equals(identity.AuthenticationType, scheme, StringComparison.OrdinalIgnoreCase)) ||
              HasSessionProvenance(identity))));
    }

    private static bool IsHumanSession(ClaimsIdentity identity)
    {
        // A validated bearer token is not necessarily interactive: client-credentials tokens
        // can carry the same scope-governed marker as user tokens. Require a positive user
        // session-provenance signal for bearer auth and reject explicit machine-grant markers.
        if (string.Equals(identity.AuthenticationType, JwtBearerDefaults.AuthenticationScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (HasMachineGrant(identity))
            {
                return false;
            }

            return HasSessionProvenance(identity);
        }

        return true;
    }

    private static bool HasSessionProvenance(ClaimsIdentity identity)
        => identity.HasClaim(c =>
            string.Equals(c.Type, InteractiveProvenanceClaimType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Value, InteractiveProvenanceClaimValue, StringComparison.OrdinalIgnoreCase));

    internal static void ReplaceWithServerDerivedProvenance(
        ClaimsPrincipal principal,
        string? configuredUserIdClaimType = null)
    {
        foreach (var identity in principal.Identities.OfType<ClaimsIdentity>())
        {
            foreach (var claim in identity.Claims
                         .Where(static claim => string.Equals(
                             claim.Type,
                             InteractiveProvenanceClaimType,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                identity.RemoveClaim(claim);
            }
        }

        if (HasMachineGrant(principal) ||
            principal.Identity is not ClaimsIdentity bearerIdentity ||
            !HasHumanSessionEvidence(bearerIdentity, configuredUserIdClaimType))
        {
            return;
        }

        bearerIdentity.AddClaim(new Claim(
            InteractiveProvenanceClaimType,
            InteractiveProvenanceClaimValue));
    }

    private static bool HasHumanSessionEvidence(
        ClaimsIdentity identity,
        string? configuredUserIdClaimType)
    {
        var hasSubject = identity.Claims.Any(claim =>
            IsSubjectClaim(claim.Type, configuredUserIdClaimType) &&
            !string.IsNullOrWhiteSpace(claim.Value));
        if (!hasSubject)
        {
            return false;
        }

        var hasHumanAuthenticationMethod = identity.Claims.Any(claim =>
            string.Equals(claim.Type, "amr", StringComparison.OrdinalIgnoreCase) &&
            HumanAuthenticationMethods.Contains(claim.Value));
        if (hasHumanAuthenticationMethod)
        {
            return true;
        }

        var hasSessionId = identity.Claims.Any(claim =>
            string.Equals(claim.Type, "sid", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(claim.Value));
        var authenticationTime = identity.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, "auth_time", StringComparison.OrdinalIgnoreCase))?.Value;
        return hasSessionId &&
            long.TryParse(authenticationTime, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0;
    }

    private static bool IsSubjectClaim(string claimType, string? configuredUserIdClaimType)
        => (!string.IsNullOrWhiteSpace(configuredUserIdClaimType) &&
            string.Equals(claimType, configuredUserIdClaimType, StringComparison.OrdinalIgnoreCase)) ||
           string.Equals(claimType, ClaimTypes.NameIdentifier, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(claimType, "sub", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(claimType, "oid", StringComparison.OrdinalIgnoreCase);

    private static bool HasMachineGrant(ClaimsPrincipal principal)
        => principal.Identities.OfType<ClaimsIdentity>().Any(HasMachineGrant);

    private static bool HasMachineGrant(ClaimsIdentity identity)
        => identity.HasClaim(c =>
            (string.Equals(c.Type, "grant_type", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(c.Type, "client_credentials", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(c.Type, "honua_auth_flow", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, "client_credentials", StringComparison.OrdinalIgnoreCase)));
}
