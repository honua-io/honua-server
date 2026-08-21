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
/// Authorization requirement for a proposal approval or rejection. API-key
/// principals must carry full admin authority or the exact
/// <c>admin:approve</c> grant; other admin identities continue to the endpoint's
/// operator-RBAC decision.
/// </summary>
internal sealed class AdminApprovalRequirement : IAuthorizationRequirement;

/// <summary>
/// Endpoint marker for the two proposal-resolution actions that accept the
/// narrowly scoped <c>admin:approve</c> grant.
/// </summary>
internal sealed class AdminApprovalEndpointMetadata
{
    private AdminApprovalEndpointMetadata()
    {
    }

    /// <summary>The singleton endpoint marker.</summary>
    public static AdminApprovalEndpointMetadata Instance { get; } = new();
}

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

        var isApprovalEndpoint = httpContext?.GetEndpoint()?.Metadata
            .GetMetadata<AdminApprovalEndpointMetadata>() is not null;

        if (AdminApiKeyPermission.IsAuthorized(context.User, method)
            || (isApprovalEndpoint && AdminApiKeyPermission.IsApprovalAuthorized(context.User)))
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

/// <summary>
/// Enforces the API-key side of the focused approval contract independently of
/// the general admin policy. This remains on the endpoint when OIDC rewrites the
/// coarse admin policy to accept configured role aliases.
/// </summary>
internal sealed class AdminApprovalAuthorizationHandler
    : AuthorizationHandler<AdminApprovalRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminApprovalRequirement requirement)
    {
        var isApiKeyPrincipal = context.User.Identities.Any(identity =>
            string.Equals(
                identity.AuthenticationType,
                AuthenticationExtensions.ApiKeyScheme,
                StringComparison.Ordinal));

        if (!isApiKeyPrincipal || AdminApiKeyPermission.IsApprovalAuthorized(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
