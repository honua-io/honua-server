// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for streaming feature operations that reduce memory pressure for large result sets
/// </summary>
public interface IStreamingFeatureStore
{
    /// <summary>
    /// Streams features matching the specified query asynchronously
    /// This method is optimized for large result sets to reduce memory pressure
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of features</returns>
    /// <remarks>
    /// Use this method when expecting large result sets (> 1000 features) to avoid loading
    /// all features into memory at once. The features are streamed directly from the database.
    /// </remarks>
    IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams features in batches matching the specified query asynchronously
    /// This method provides better control over memory usage and network traffic
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="batchSize">Number of features per batch (default: 1000)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature batches</returns>
    /// <remarks>
    /// Use this method when you need to process features in controlled batches,
    /// such as for API responses that need to maintain reasonable response sizes.
    /// </remarks>
    IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId,
        FeatureQuery query,
        int batchSize = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams features with their geometries in GML format (for OGC compliance)
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of GML features</returns>
    /// <remarks>
    /// This method is specifically designed for OGC API Features endpoints
    /// that need to stream large datasets with GML geometry representation.
    /// </remarks>
    IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}

