// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Revokes portal OAuth refresh tokens for a managed user. Deletion removes the user, so a
/// later membership lookup cannot see them; the revocation marker still rejects refresh
/// tokens issued before the deprovision.
/// </summary>
internal static class PortalOAuthRefreshRevoker
{
    /// <summary>
    /// Revokes refresh tokens for the user's id, external subject, and federated reference.
    /// A null store or user is a no-op so hosts without the OAuth bridge still deprovision.
    /// </summary>
    public static async Task RevokeManagedUserAsync(
        PortalOAuthStore? store,
        ManagedUser? user,
        CancellationToken cancellationToken)
    {
        if (store is null || user is null)
        {
            return;
        }

        await store.RevokePrincipalAsync(user.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(user.ExternalId))
        {
            return;
        }

        await store.RevokePrincipalAsync(user.ExternalId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(user.ExternalIssuer))
        {
            await store.RevokePrincipalAsync(
                user.ExternalIssuer + "|" + user.ExternalId,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
