// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature store capability for GeoJSON-encoded paged queries that avoid exact total counts.
/// </summary>
public interface IPagedGeoJsonFeatureStore
{
    /// <summary>
    /// Queries a single page of GeoJSON-encoded features and reports whether additional results exist.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="query">Query parameters including paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged query result without requiring an exact total count.</returns>
    Task<PagedQueryResult<EncodedGeoJsonFeature>> QueryGeoJsonPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
