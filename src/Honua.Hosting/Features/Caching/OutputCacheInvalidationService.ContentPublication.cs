// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Content publication cache invalidation. Route pointer and policy changes evict the
/// tagged published-route responses so subsequent public reads reflect the new active
/// version, visibility, or embed policy.
/// </summary>
internal sealed partial class OutputCacheInvalidationService
{
    /// <summary>
    /// Invalidates cached published-route responses for a publication after a route
    /// pointer (publish/republish/rollback) or policy change.
    /// </summary>
    public async Task InvalidatePublicationAsync(string? publicationId, string? routeSlug, CancellationToken cancellationToken)
    {
        var tags = new List<string> { ContentPublicationCacheTags.All };
        if (!string.IsNullOrWhiteSpace(publicationId))
        {
            tags.Add(ContentPublicationCacheTags.Publication(publicationId));
        }

        if (!string.IsNullOrWhiteSpace(routeSlug))
        {
            tags.Add(ContentPublicationCacheTags.Route(routeSlug));
        }

        await EvictTagsAsync(tags, cancellationToken).ConfigureAwait(false);
    }
}
