// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.SharedCaching;

namespace Honua.Infrastructure.Caching;

internal sealed class AuthenticationResponseCacheMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // The one callback that runs last: callbacks run in reverse order of registration, so this
        // middleware is first in the pipeline. Later schemes can hydrate the principal, and no
        // endpoint or ETag callback can overwrite the final policy.
        context.Response.OnStarting(static state =>
        {
            var context = (HttpContext)state;
            AuthenticationResponseCachePolicy.Apply(context);
            SharedCacheGuard.Apply(context);
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
