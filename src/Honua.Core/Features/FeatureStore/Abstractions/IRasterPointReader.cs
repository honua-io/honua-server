// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides an optimized query path for projected point coordinates used by raster rendering.
/// </summary>
public interface IRasterPointReader
{
    /// <summary>
    /// Queries projected point coordinates for a layer using the supplied feature query constraints.
    /// </summary>
    Task<ImmutableArray<ProjectedPoint>> QueryProjectedPointsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
