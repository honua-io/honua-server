// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides read-only access to features with query and aggregation capabilities.
/// Segregated from write operations to support read-only consumers and caching strategies.
/// </summary>
public interface IFeatureReader
{
    /// <summary>
    /// Retrieves a feature by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Unique feature identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Feature if found, null otherwise</returns>
    Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features with optional filtering and pagination
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated query result with total count</returns>
    Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts features matching the specified criteria
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification for filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of features matching the criteria</returns>
    Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the spatial extent (bounding box) of features
    /// </summary>
    /// <param name="layerId">Layer identifier to analyze</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Spatial extent of the features, null if no features found</returns>
    Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the temporal extent (min/max) for a temporal field.
    /// </summary>
    /// <param name="layerId">Layer identifier to analyze</param>
    /// <param name="fieldName">Temporal field name</param>
    /// <param name="fieldType">Temporal field type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Temporal extent of the field, null if no values found</returns>
    Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        string fieldName,
        FieldType fieldType,
        CancellationToken cancellationToken = default);
}
