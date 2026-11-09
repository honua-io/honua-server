// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature store capability for returning GML-encoded geometries.
/// </summary>
public interface IGmlFeatureStore
{
    /// <summary>
    /// Queries features with geometry encoded as GML fragments.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="query">Query parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with GML-encoded geometries.</returns>
    Task<QueryResult<GmlFeature>> QueryGmlAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
