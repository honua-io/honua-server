// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Resolves live membership for identities mirrored into the managed-user store by SCIM,
/// OIDC provisioning, or the admin identity surface.
/// </summary>
/// <remarks>
/// The lookup identifier is the OIDC subject captured at submission time
/// (<c>NameIdentifier</c>/<c>sub</c>), while SCIM keys <see cref="Honua.Core.Features.Identity.Domain.ManagedUser.UserId"/>
/// by the IdP's <c>userName</c>. When those namespaces coincide the direct lookup hits; when
/// they differ the SCIM <c>externalId</c> persisted on the record bridges the subject to the
/// provisioned user (honua-server#3081). In multi-node deployments the registered
/// <see cref="IUserStore"/> is the Redis-backed durable store whenever durable workflows are
/// enabled, so provisioning handled on one replica is authoritative for deferred firings and
/// approval resumes on every other replica.
/// </remarks>
internal sealed class ManagedUserPrincipalMembershipSource(IUserStore userStore)
    : IPrincipalMembershipSource
{
    public async Task<PrincipalMembership?> ResolveMembershipAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        var user = await userStore.GetUserAsync(principalId, cancellationToken).ConfigureAwait(false)
            ?? await userStore.FindByExternalIdAsync(principalId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        return new PrincipalMembership(
            user.IsActive,
            user.IsActive ? user.Roles : []);
    }
}
