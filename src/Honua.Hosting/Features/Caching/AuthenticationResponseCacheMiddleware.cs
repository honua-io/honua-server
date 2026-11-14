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
            ApplyFinalPolicy(context);
            return Task.CompletedTask;
        }, context);

        return next(context);
    }

    internal static void ApplyFinalPolicy(HttpContext context)
    {
        // Streaming endpoints forbid intermediaries from buffering or transforming frames.
        // Storage restrictions below may replace Cache-Control, but must retain this directive.
        var noTransform = context.Response.GetTypedHeaders().CacheControl?.NoTransform == true;
        AuthenticationResponseCachePolicy.Apply(context);
        OutputCacheStatus.Apply(context);
        SharedCacheGuard.Apply(context);
        if (noTransform && context.Response.GetTypedHeaders().CacheControl is { } cacheControl)
        {
            cacheControl.NoTransform = true;
            context.Response.GetTypedHeaders().CacheControl = cacheControl;
        }
    }
}
