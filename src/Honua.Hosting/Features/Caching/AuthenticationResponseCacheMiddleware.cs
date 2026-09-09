// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Applies client cache protection after authentication and response headers are finalized.
/// </summary>
internal sealed class AuthenticationResponseCacheMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Register before authentication: later schemes can hydrate the principal,
        // and endpoint/ETag callbacks must not overwrite the final no-store policy.
        context.Response.OnStarting(static state =>
        {
            AuthenticationResponseCachePolicy.Apply((HttpContext)state);
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
