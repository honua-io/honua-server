// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Operations;

/// <summary>Revokes a server-minted approved-operation credential and verifies the result.</summary>
internal static class ApprovedOperationCredentialRevocation
{
    public static async Task RevokeAsync(IAdminApiKeyStore store, Guid credentialId)
    {
        ArgumentNullException.ThrowIfNull(store);

        var revoked = await store.RevokeAsync(credentialId, CancellationToken.None).ConfigureAwait(false);
        if (revoked?.RevokedAt is null)
        {
            throw new InvalidOperationException(
                $"Failed to revoke approved-operation credential '{credentialId:D}'.");
        }
    }
}
