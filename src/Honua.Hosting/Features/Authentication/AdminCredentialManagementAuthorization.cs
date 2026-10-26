// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Authorization requirement for endpoints that create, rotate or revoke credentials or
/// change identity authority. Added on top of the admin policy so general admin write
/// (<c>admin:write</c>, <c>admin:manage</c>) is not enough.
/// </summary>
internal sealed class AdminCredentialManagementRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="AdminCredentialManagementRequirement"/> with
/// <see cref="AdminApiKeyPermission.HasCredentialManagementAuthority"/>.
/// </summary>
internal sealed class AdminCredentialManagementAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<AdminCredentialManagementAuthorizationHandler> logger)
    : AuthorizationHandler<AdminCredentialManagementRequirement>
{
    internal const string MissingAuthorityCode = "admin_authorization/missing_credential_management_authority";

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<AdminCredentialManagementAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminCredentialManagementRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        var method = httpContext?.Request.Method;

        if (AdminApiKeyPermission.HasCredentialManagementAuthority(context.User, method, httpContext?.Request.Path.Value))
        {
            context.Succeed(requirement);
        }
        else
        {
            AuthenticationLog.CredentialManagementDenied(_logger, method ?? "(unknown)");
            context.Fail(new AuthorizationFailureReason(this, MissingAuthorityCode));
        }

        return Task.CompletedTask;
    }
}
