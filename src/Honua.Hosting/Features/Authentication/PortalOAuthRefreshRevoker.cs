// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Infrastructure.Authentication;

// Deleting a user removes the membership lookup, so the revocation marker still rejects a refresh token issued before that deletion.
internal static class PortalOAuthRefreshRevoker
{
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
