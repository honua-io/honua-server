// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides paged raw GeoServices point features for protocol-level JSON fast paths.
/// </summary>
public interface IPagedRawGeoServicesFeatureStore
{
    /// <summary>
    /// Queries a single page of raw GeoServices point features for the supplied layer.
    /// </summary>
    /// <param name="layerId">The identifier of the layer to query.</param>
    /// <param name="query">The canonical feature query describing the page.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A paged result of raw GeoServices point features.</returns>
    Task<PagedQueryResult<RawGeoServicesFeature>> QueryGeoServicesRawPointPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
