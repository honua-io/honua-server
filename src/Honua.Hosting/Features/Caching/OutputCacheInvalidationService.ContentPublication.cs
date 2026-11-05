// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
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
