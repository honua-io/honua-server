// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Portal.Sharing;

internal static class PortalAudienceHydrationExtensions
{
    internal static IApplicationBuilder UsePortalAudienceHydration(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            await PortalAudienceSnapshot.RefreshAsync(context, context.RequestAborted).ConfigureAwait(false);

            await next().ConfigureAwait(false);
        });
}
