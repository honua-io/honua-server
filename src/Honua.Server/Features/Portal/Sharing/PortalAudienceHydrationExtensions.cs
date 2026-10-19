// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Portal.Sharing;

internal static class PortalAudienceHydrationExtensions
{
    internal static IApplicationBuilder UsePortalAudienceHydration(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var provider = context.RequestServices.GetService<IPortalAudienceSnapshotProvider>();
            if (provider is not null && context.User.Identity?.IsAuthenticated == true
                && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } username
                && PortalResourceReadAuthorizer.TryGetTrustedTenant(context, out var tenantId))
            {
                // No TTL: each request gets one fresh, internally consistent audience
                // view. A database failure prevents a success-shaped stale grant.
                var serviceIds = await provider.GetServiceIdsAsync(
                    username, tenantId, context.RequestAborted).ConfigureAwait(false);
                PortalAudienceSnapshot.Set(context, serviceIds);
            }

            await next().ConfigureAwait(false);
        });
}
