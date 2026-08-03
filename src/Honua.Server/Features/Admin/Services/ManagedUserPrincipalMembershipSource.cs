// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Resolves live membership for identities mirrored into the managed-user store by SCIM,
/// OIDC provisioning, or the admin identity surface.
/// </summary>
internal sealed class ManagedUserPrincipalMembershipSource(IUserStore userStore)
    : IPrincipalMembershipSource
{
    public async Task<PrincipalMembership?> ResolveMembershipAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        var user = await userStore.GetUserAsync(principalId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        return new PrincipalMembership(
            user.IsActive,
            user.IsActive ? user.Roles : []);
    }
}
