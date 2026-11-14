// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;

namespace Honua.Server.Features.Portal.Sharing;

internal static class PortalAudienceHydrationExtensions
{
    internal static IApplicationBuilder UsePortalAudienceHydration(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var provider = context.RequestServices.GetService<IPortalAudienceSnapshotProvider>();
            if (provider is not null && context.User.Identity?.IsAuthenticated == true
                && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } username
                && PortalResourceReadAuthorizer.TryGetTrustedTenant(context, out _))
            {
                // No TTL: each request gets one fresh, internally consistent audience
                // view. A database failure prevents a success-shaped stale grant.
                var serviceIds = await provider.GetServiceIdsAsync(
                    username, TenantReadScope.For(context.RequestServices.GetService<ITenantContext>()), context.RequestAborted).ConfigureAwait(false);
                PortalAudienceSnapshot.Set(context, serviceIds);
            }

            await next().ConfigureAwait(false);
        });
}
