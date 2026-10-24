// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature store capability for paged queries that avoid exact total counts.
/// </summary>
public interface IPagedFeatureReader
{
    /// <summary>
    /// Queries a single page of features and reports whether additional results exist.
    /// </summary>
    /// <param name="layerId">Layer identifier to query.</param>
    /// <param name="query">Query specification including paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged query result without requiring an exact total count.</returns>
    Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
