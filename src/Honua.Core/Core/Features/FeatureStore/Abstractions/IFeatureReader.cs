// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IFeatureReader
{
    Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        string fieldName,
        TemporalPropertyType propertyType,
        CancellationToken cancellationToken = default);

    Task<EstimateResult> GetEstimatesAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        CancellationToken cancellationToken = default);
}
