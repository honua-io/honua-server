// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;
using MobileFeature = Honua.Mobile.Core.Models.Feature;
using MobileExtent = Honua.Mobile.Core.Models.Extent;

namespace Honua.Mobile.Core.Abstractions;

/// <summary>
/// Provides read access to geospatial features using gRPC protocols.
/// </summary>
public interface IFeatureReader
{
    /// <summary>
    /// Queries features and returns all results in a single response.
    /// Use for small result sets or when you need metadata immediately.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result with features and metadata</returns>
    Task<QueryResult<MobileFeature>> QueryAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams features efficiently for large result sets.
    /// Features are yielded as they are received from the server.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of features</returns>
    IAsyncEnumerable<MobileFeature> QueryStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts features matching the query without returning feature data.
    /// </summary>
    Task<long> CountAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the extent (bounding box) of features matching the query.
    /// </summary>
    Task<MobileExtent?> GetExtentAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets object IDs of features matching the query without returning feature data.
    /// </summary>
    Task<IReadOnlyList<long>> GetObjectIdsAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}