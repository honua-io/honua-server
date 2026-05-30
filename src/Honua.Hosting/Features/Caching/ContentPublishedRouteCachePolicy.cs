// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy for public published-route reads. Tags responses for
/// tag-based invalidation and disables caching for security-sensitive reads
/// (public-link or embed reads) so only anonymous, link-free public reads are
/// cached. The global anonymous-only base policy additionally prevents caching of
/// authenticated requests, so cached entries never vary by principal.
/// </summary>
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
