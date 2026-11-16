// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

internal interface IFeatureDataAccess
{
    Task<long> ExecuteCountQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    Task<ImmutableArray<ProjectedPoint>> ExecuteSelectProjectedPointsAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    Task<ImmutableArray<EncodedGeoJsonFeature>> ExecuteSelectGeoJsonQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<ImmutableArray<RawGeoJsonFeature>> ExecuteSelectRawGeoJsonQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<byte[]?> ExecuteSelectGeobufQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    // Providers without a row-at-a-time reader fall back to the collected ids.
    async IAsyncEnumerable<long> StreamSelectObjectIdsQueryAsync(
        ObjectIdQueryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var objectIds = await ExecuteSelectObjectIdsQueryAsync(request.Query, request.FeatureQuery, request.LayerId, cancellationToken).ConfigureAwait(false);
        foreach (var objectId in objectIds)
        {
            yield return objectId;
        }
    }

    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken);

    Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken);

    Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken);

    Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken);

    Task<Feature> CreateFeatureAsync(FeatureWritePolicy policy, Feature feature, CancellationToken cancellationToken)
        => policy.RowFilter is null
            ? CreateFeatureAsync(policy.LayerId, feature, cancellationToken)
            : throw new NotSupportedException("The provider does not support row-policy checked writes.");

    Task<Feature> UpdateFeatureAsync(FeatureWritePolicy policy, Feature feature, CancellationToken cancellationToken)
        => policy.RowFilter is null
            ? UpdateFeatureAsync(policy.LayerId, feature, cancellationToken)
            : throw new NotSupportedException("The provider does not support row-policy checked writes.");

    Task<bool> DeleteFeatureAsync(FeatureWritePolicy policy, long featureId, CancellationToken cancellationToken)
        => policy.RowFilter is null
            ? DeleteFeatureAsync(policy.LayerId, featureId, cancellationToken)
            : throw new NotSupportedException("The provider does not support row-policy checked writes.");

    Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken);

    Task<FeatureExtent?> GetExtentAsync(
        int layerId,
        ParameterizedQuery? query,
        FeatureQuery featureQuery,
        CancellationToken cancellationToken);

    Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        ParameterizedQuery? query,
        CancellationToken cancellationToken);

    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, long maxTileSize, CancellationToken cancellationToken);

    IAsyncEnumerable<Feature> StreamFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, CancellationToken cancellationToken);

    IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, CancellationToken cancellationToken);

    Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken);

    Task<FeatureEditApplication> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditIdempotencyReceipt receipt,
        CancellationToken cancellationToken)
    {
        _ = receipt;
        return ApplyWithoutDurableReceiptAsync();

        async Task<FeatureEditApplication> ApplyWithoutDurableReceiptAsync()
        {
            var result = await ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            return new FeatureEditApplication { Result = result, ReceiptDurable = false };
        }
    }

    Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This data provider does not support cross-layer transactions.");
}
