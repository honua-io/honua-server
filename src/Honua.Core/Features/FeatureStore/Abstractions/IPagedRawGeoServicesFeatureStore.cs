// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides paged raw GeoServices point features for protocol-level JSON fast paths.
/// </summary>
public interface IPagedRawGeoServicesFeatureStore
{
    Task<PagedQueryResult<RawGeoServicesFeature>> QueryGeoServicesRawPointPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
