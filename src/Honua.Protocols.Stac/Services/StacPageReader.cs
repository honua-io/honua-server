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
        var result = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return PagedQueryResult<Feature>.Create(result.Features, result.HasMoreResults, result.TotalCount);
    }
}
