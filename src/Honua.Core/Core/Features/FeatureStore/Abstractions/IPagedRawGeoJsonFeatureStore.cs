// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature store capability for paged GeoJSON queries that preserve raw properties JSON.
/// </summary>
public interface IPagedRawGeoJsonFeatureStore
{
    /// <summary>
    /// Queries a single page of raw GeoJSON features and reports whether additional results exist.
    /// </summary>
    Task<PagedQueryResult<RawGeoJsonFeature>> QueryGeoJsonRawPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
