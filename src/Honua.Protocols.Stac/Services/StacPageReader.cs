// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Stac.Services;

internal static class StacPageReader
{
    internal static async Task<PagedQueryResult<Feature>> ReadAsync(
        IFeatureReader reader,
        int layerId,
        FeatureQuery query,
        bool omitCount,
        CancellationToken cancellationToken,
        long? knownCount = null)
    {
        if ((omitCount || knownCount.HasValue) && reader is IPagedFeatureReader pagedReader)
        {
            var page = await pagedReader.QueryPageAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return page with { TotalCount = omitCount ? null : knownCount ?? page.TotalCount };
        }

        // Providers without the optional capability retain their normal query behavior.
        var result = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var hasMore = result.HasMoreResults || result.TotalCount > (long)(query.Offset ?? 0) + result.Features.Length;
        return PagedQueryResult<Feature>.Create(result.Features, hasMore, omitCount ? null : knownCount ?? result.TotalCount);
    }
}
