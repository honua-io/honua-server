// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for database data access operations
/// </summary>
internal interface IFeatureDataAccess
{
    /// <summary>
    /// Executes a count query and returns the result
    /// </summary>
    Task<long> ExecuteCountQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a select query and returns features
    /// </summary>
    Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a select query and returns object IDs.
    /// </summary>
    Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a select query and returns GML features
    /// </summary>
    Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a provider-native FlatGeobuf query and returns raw bytes.
    /// </summary>
    Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a related records query and returns matching features
    /// </summary>
    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a single feature
    /// </summary>
    Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken);

    /// <summary>
    /// Updates a single feature
    /// </summary>
    Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a single feature
    /// </summary>
    Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a single feature by ID
    /// </summary>
    Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Calculates feature extent
    /// </summary>
    Task<FeatureExtent?> GetExtentAsync(
        int layerId,
        ParameterizedQuery? query,
        FeatureQuery featureQuery,
        CancellationToken cancellationToken);

    /// <summary>
    /// Calculates temporal extent for a temporal field.
    /// </summary>
    Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        ParameterizedQuery? query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes an aggregate statistics query and returns rows of field-value pairs
    /// </summary>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        ParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates MVT tile data
    /// </summary>
    Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Streams features for large result sets
    /// </summary>
    IAsyncEnumerable<Feature> StreamFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, CancellationToken cancellationToken);

    /// <summary>
    /// Streams GML features for large result sets
    /// </summary>
    IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, CancellationToken cancellationToken);

    /// <summary>
    /// Processes batch edits with transaction support
    /// </summary>
    Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken);
}
