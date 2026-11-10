// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

internal sealed class SharedCacheGuardMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Callbacks run in reverse order of registration, so registering first makes this the final word on Cache-Control.
        context.Response.OnStarting(static state =>
        {
            SharedCacheGuard.Apply((HttpContext)state);
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
