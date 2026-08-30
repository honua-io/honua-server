// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Narrow authorization requirement for deciding an existing operation proposal.
/// </summary>
internal sealed class AdminApproveRequirement : IAuthorizationRequirement;

internal sealed class AdminApproveAuthorizationHandler : AuthorizationHandler<AdminApproveRequirement>
{
    internal const string MissingGrantCode = "admin_authorization/missing_admin_approve_grant";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminApproveRequirement requirement)
    {
        var isApiKey = context.User.Identities.Any(identity =>
            string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal));
        if (!isApiKey ||
            AdminApiKeyPermission.ResolveAccessLevel(context.User) == AdminApiKeyPermission.AdminAccessLevel.Write ||
            AdminApiKeyPermission.HasApproveGrant(context.User))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, MissingGrantCode));
        }

        return Task.CompletedTask;
    }
}
