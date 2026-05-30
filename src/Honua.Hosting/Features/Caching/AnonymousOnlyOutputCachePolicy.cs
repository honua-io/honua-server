// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy that only allows caching for anonymous requests.
/// Prevents serving cached responses to authenticated users with differing access policies.
/// </summary>
internal sealed class AnonymousOnlyOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (IsAuthenticated(context.HttpContext))
        {
            DisableCaching(context);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (IsAuthenticated(context.HttpContext))
        {
            context.AllowCacheLookup = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (IsAuthenticated(context.HttpContext))
        {
            context.AllowCacheStorage = false;
            return ValueTask.CompletedTask;
        }

        var statusCode = context.HttpContext.Response.StatusCode;
        if (statusCode == StatusCodes.Status401Unauthorized || statusCode == StatusCodes.Status403Forbidden)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsAuthenticated(HttpContext context)
        => context.User?.Identity?.IsAuthenticated == true;

    private static void DisableCaching(OutputCacheContext context)
    {
        context.EnableOutputCaching = false;
        context.AllowCacheLookup = false;
        context.AllowCacheStorage = false;
    }
}
