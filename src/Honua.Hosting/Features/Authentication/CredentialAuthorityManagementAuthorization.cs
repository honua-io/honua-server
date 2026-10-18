// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Additional boundary for changing credential or identity authority. Always compose
/// with the existing administrative policy; this guard grants no administrative access.
/// </summary>
internal static class CredentialAuthorityManagementAuthorization
{
    internal const string PolicyName = "NonDelegatedCredentialAuthorityManagement";

    internal static void Configure(AuthorizationOptions options) => options.AddPolicy(PolicyName,
        policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
            !AgentCredentialAuthority.IsDelegated(context.User)));
}
