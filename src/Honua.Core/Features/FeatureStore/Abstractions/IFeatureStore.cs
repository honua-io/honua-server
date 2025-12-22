// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for feature storage and retrieval operations
/// </summary>
public interface IFeatureStore
{
    // Query operations

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
    /// Queries features related to the specified source features through a relationship
    /// </summary>
    /// <param name="layerId">Source layer identifier</param>
    /// <param name="query">Related query specification including object IDs and relationship</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result containing related features</returns>
    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default);

    // Edit operations

    /// <summary>
    /// Creates a new feature
    /// </summary>
    /// <param name="layerId">Layer identifier where the feature should be created</param>
    /// <param name="feature">Feature to create (Id may be ignored and auto-generated)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created feature with assigned ID</returns>
    Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing feature
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="feature">Feature with updated values (must include valid Id)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated feature</returns>
    /// <exception cref="InvalidOperationException">Thrown if feature with the specified ID does not exist</exception>
    Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a feature by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Unique feature identifier to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if feature was deleted, false if not found</returns>
    Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    // Batch operations

    /// <summary>
    /// Applies a batch of create, update, and delete operations in a single transaction
    /// </summary>
    /// <param name="layerId">Layer identifier for the operations</param>
    /// <param name="editBatch">Batch of operations to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result summary of the batch operation</returns>
    Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default);
}
