// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Abstractions;

public interface ISpatialAnalyticsReader
{
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QuerySpatialJoinAsync(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBufferAggregateAsync(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDensityAsync(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        CancellationToken cancellationToken = default);
}
