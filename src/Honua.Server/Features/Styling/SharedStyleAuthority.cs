// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Styling;

internal static class SharedStyleAuthority
{
    internal static async Task RequireCurrentKeyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var id = context.User.FindFirst("api_key_id")?.Value;
        if (id is null) return; // Human bearer/bootstrap follows its own authenticated policy.
        if (!Guid.TryParse(id, out var keyId) || keyId == Guid.Empty) Deny();
        var record = await context.RequestServices.GetRequiredService<IAdminApiKeyStore>().GetAsync(keyId, cancellationToken).ConfigureAwait(false);
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now ||
            !record.Permissions.Order(StringComparer.Ordinal).SequenceEqual(context.User.FindAll("permission")
                .Select(claim => claim.Value).Order(StringComparer.Ordinal))) Deny();
        var actual = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(record!.Id, record.Name, record.Permissions);
        if (CanonicalSecurityActor.Resolve(actual)?.ActorId != CanonicalSecurityActor.Resolve(context.User)?.ActorId) Deny();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}
