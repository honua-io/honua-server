// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// A public-link or embed read is not cached; only an anonymous read with no link is.
internal sealed class ContentPublishedRouteCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var query = context.HttpContext.Request.Query;
        if (query.ContainsKey("link") || query.ContainsKey("token") || query.ContainsKey("embed"))
        {
            // Link/embed reads are authorization-sensitive and must not be cached by slug.
            context.EnableOutputCaching = false;
            return ValueTask.CompletedTask;
        }

        AddTags(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddTags(context);
        return ValueTask.CompletedTask;
    }

    private static void AddTags(OutputCacheContext context)
    {
        context.Tags.Add(ContentPublicationCacheTags.All);
        var slug = ContentPublicationCacheTags.ReadRouteSlug(context.HttpContext.Request.RouteValues);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            context.Tags.Add(ContentPublicationCacheTags.Route(slug));
        }
    }
}
